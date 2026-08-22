using System.Security.Cryptography;
using System.Text.Json;

using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

using NativeDCB.Actors.Storage;

namespace NativeDCB.Server.Security;

internal sealed class ApiKeyStore
{
    private const string CredentialPrefix = "ndcb1_";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly SemaphoreSlim _gate = new(initialCount: 1, maxCount: 1);
    private readonly NativeDcbAuthenticationOptions _options;
    private readonly string _path;
    private readonly TimeProvider _timeProvider;
    private volatile ApiKeyCatalog _catalog = ApiKeyCatalog.Empty;
    private volatile bool _initialized;

    public ApiKeyStore(
        IOptions<NativeDcbAuthenticationOptions> authentication,
        IOptions<ActorStorageOptions> storage,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(authentication);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _options = authentication.Value;
        _timeProvider = timeProvider;
        string configured = _options.ApiKeys.StorePath;
        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new InvalidOperationException("Authentication:ApiKeys:StorePath must be non-empty.");
        }

        _path = Path.GetFullPath(Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(storage.Value.DatabaseRoot, configured));
    }

    public bool Enabled => _options.HasProvider(AuthenticationProviderNames.ApiKey);

    public bool HasActiveBootstrap => _catalog.Keys.Any(key => key.Bootstrap && key.RevokedUtc is null);

    public async Task<CreatedApiKey?> InitializeAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized || !Enabled)
            {
                return null;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            using FileStream catalogLock = AcquireCatalogLock();
            _catalog = await ReadAsync(cancellationToken).ConfigureAwait(false);
            Validate(_catalog);

            CreatedApiKey? bootstrap = null;
            if (_catalog.Keys.Length == 0 && _options.ApiKeys.BootstrapOnEmptyStore)
            {
                bootstrap = Generate(
                    "bootstrap",
                    [PermissionGrant.Superuser],
                    expiresUtc: null,
                    bootstrap: true);
                _catalog = new ApiKeyCatalog(ApiKeyCatalog.CurrentSchema, [bootstrap.Record]);
                await WriteAsync(_catalog, cancellationToken).ConfigureAwait(false);
            }

            _initialized = true;
            return bootstrap;
        }
        finally
        {
            _gate.Release();
        }
    }

    public ApiKeyRecord? Authenticate(string credential)
    {
        if (!_initialized || !TryParseCredential(credential, out string keyId, out byte[] secret))
        {
            return null;
        }

        ApiKeyRecord? record = _catalog.Keys.SingleOrDefault(key =>
            string.Equals(key.KeyId, keyId, StringComparison.Ordinal));
        if (record is null || record.RevokedUtc is not null ||
            record.ExpiresUtc is { } expires && expires <= _timeProvider.GetUtcNow())
        {
            return null;
        }

        byte[] expected;
        try
        {
            expected = Convert.FromBase64String(record.SecretHash);
        }
        catch (FormatException)
        {
            return null;
        }

        byte[] actual = SHA256.HashData(secret);
        return expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(expected, actual)
            ? record
            : null;
    }

    public async Task<CreatedApiKey> CreateAsync(
        string label,
        IReadOnlyCollection<PermissionGrant> permissions,
        DateTimeOffset? expiresUtc,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        if (label.Length > 200)
        {
            throw new ArgumentException("API-key labels cannot exceed 200 characters.", nameof(label));
        }

        if (permissions.Count == 0)
        {
            throw new ArgumentException("At least one permission is required.", nameof(permissions));
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        if (expiresUtc is not null && expiresUtc <= now)
        {
            throw new ArgumentOutOfRangeException(nameof(expiresUtc), "API-key expiry must be in the future.");
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using FileStream catalogLock = AcquireCatalogLock();
            string[] values = permissions.Select(permission => permission.ToString())
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            CreatedApiKey created = Generate(label, values, expiresUtc, bootstrap: false);
            ApiKeyRecord[] keys = _catalog.Keys.Select(key =>
                    key.Bootstrap && key.RevokedUtc is null ? key with { RevokedUtc = now } : key)
                .Append(created.Record)
                .ToArray();
            ApiKeyCatalog replacement = new(ApiKeyCatalog.CurrentSchema, keys);
            await WriteAsync(replacement, cancellationToken).ConfigureAwait(false);
            _catalog = replacement;
            return created;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CreatedApiKey> RecoverBootstrapAsync(CancellationToken cancellationToken)
    {
        if (!Enabled)
        {
            throw new InvalidOperationException("The API-key authentication provider is not enabled.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            using FileStream catalogLock = AcquireCatalogLock();
            ApiKeyCatalog current = await ReadAsync(cancellationToken).ConfigureAwait(false);
            Validate(current);
            DateTimeOffset now = _timeProvider.GetUtcNow();
            CreatedApiKey replacement = Generate(
                "bootstrap-recovery",
                [PermissionGrant.Superuser],
                expiresUtc: null,
                bootstrap: true);
            ApiKeyRecord[] keys = current.Keys.Select(key =>
                    key.Bootstrap && key.RevokedUtc is null ? key with { RevokedUtc = now } : key)
                .Append(replacement.Record)
                .ToArray();
            _catalog = new ApiKeyCatalog(ApiKeyCatalog.CurrentSchema, keys);
            await WriteAsync(_catalog, cancellationToken).ConfigureAwait(false);
            _initialized = true;
            return replacement;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ApiKeyRecord>> ListAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return _catalog.Keys.OrderBy(key => key.CreatedUtc).ThenBy(key => key.KeyId, StringComparer.Ordinal).ToArray();
    }

    public async Task<ApiKeyRecord?> RevokeAsync(string keyId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int index = Array.FindIndex(_catalog.Keys, key =>
                string.Equals(key.KeyId, keyId, StringComparison.Ordinal));
            if (index < 0)
            {
                return null;
            }

            ApiKeyRecord current = _catalog.Keys[index];
            if (current.RevokedUtc is not null)
            {
                return current;
            }

            using FileStream catalogLock = AcquireCatalogLock();
            ApiKeyRecord revoked = current with { RevokedUtc = _timeProvider.GetUtcNow() };
            ApiKeyRecord[] keys = [.. _catalog.Keys];
            keys[index] = revoked;
            ApiKeyCatalog replacement = new(ApiKeyCatalog.CurrentSchema, keys);
            await WriteAsync(replacement, cancellationToken).ConfigureAwait(false);
            _catalog = replacement;
            return revoked;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (!_initialized)
        {
            _ = await InitializeAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private CreatedApiKey Generate(
        string label,
        string[] permissions,
        DateTimeOffset? expiresUtc,
        bool bootstrap)
    {
        byte[] id = RandomNumberGenerator.GetBytes(count: 16);
        byte[] secret = RandomNumberGenerator.GetBytes(count: 32);
        string keyId = WebEncoders.Base64UrlEncode(id);
        string encodedSecret = WebEncoders.Base64UrlEncode(secret);
        ApiKeyRecord record = new(
            keyId,
            label,
            Convert.ToBase64String(SHA256.HashData(secret)),
            permissions,
            _timeProvider.GetUtcNow(),
            expiresUtc,
            RevokedUtc: null,
            bootstrap);
        return new CreatedApiKey($"{CredentialPrefix}{keyId}.{encodedSecret}", record);
    }

    private FileStream AcquireCatalogLock()
    {
        return new FileStream(
            _path + ".lock",
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 1,
            FileOptions.None);
    }

    private async Task<ApiKeyCatalog> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return ApiKeyCatalog.Empty;
        }

        await using FileStream stream = new(
            _path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, FileOptions.Asynchronous);
        return await JsonSerializer.DeserializeAsync<ApiKeyCatalog>(stream, JsonOptions, cancellationToken)
                   .ConfigureAwait(false)
               ?? throw new InvalidDataException("The API-key catalog is empty.");
    }

    private async Task WriteAsync(ApiKeyCatalog catalog, CancellationToken cancellationToken)
    {
        string temporary = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (FileStream stream = new(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, catalog, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static bool TryParseCredential(string credential, out string keyId, out byte[] secret)
    {
        keyId = string.Empty;
        secret = [];
        if (!credential.StartsWith(CredentialPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        int separator = credential.IndexOf('.', CredentialPrefix.Length);
        if (separator <= CredentialPrefix.Length || separator == credential.Length - 1)
        {
            return false;
        }

        keyId = credential[CredentialPrefix.Length..separator];
        try
        {
            byte[] id = WebEncoders.Base64UrlDecode(keyId);
            secret = WebEncoders.Base64UrlDecode(credential[(separator + 1)..]);
            return id.Length == 16 && secret.Length == 32 &&
                   string.Equals(WebEncoders.Base64UrlEncode(id), keyId, StringComparison.Ordinal);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static void Validate(ApiKeyCatalog catalog)
    {
        if (!string.Equals(catalog.Schema, ApiKeyCatalog.CurrentSchema, StringComparison.Ordinal) ||
            catalog.Keys is null)
        {
            throw new InvalidDataException("The API-key catalog schema is invalid.");
        }

        HashSet<string> ids = new(StringComparer.Ordinal);
        foreach (ApiKeyRecord key in catalog.Keys)
        {
            // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract -- JSON can violate annotations.
            if (string.IsNullOrWhiteSpace(key.KeyId) || !ids.Add(key.KeyId) ||
                string.IsNullOrWhiteSpace(key.Label) || key.Permissions is null || key.Permissions.Length == 0 ||
                key.Permissions.Any(permission => !PermissionGrant.TryParse(permission, out _)))
            {
                throw new InvalidDataException("The API-key catalog contains an invalid record.");
            }

            try
            {
                if (Convert.FromBase64String(key.SecretHash).Length != 32)
                {
                    throw new InvalidDataException("The API-key catalog contains an invalid secret digest.");
                }
            }
            catch (FormatException exception)
            {
                throw new InvalidDataException("The API-key catalog contains an invalid secret digest.", exception);
            }
        }
    }
}
