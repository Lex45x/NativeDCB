using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Options;

using NativeDCB.Engine.Actors.Contracts;
using NativeDCB.Engine.Storage.EventLog;
using NativeDCB.Server.Catalog;

namespace NativeDCB.Server.Databases;

public sealed class DatabaseRegistry : IAsyncDisposable, IDatabaseStoreProvider
{
    private const string CatalogFileName = "catalog_v1.json";

    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly Dictionary<string, DatabaseEntry> _databases = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(initialCount: 1, maxCount: 1);
    private readonly int _maxEventCountPerPartition;
    private readonly string _root;

    public DatabaseRegistry(IOptions<ServerOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _root = Path.GetFullPath(options.Value.DatabaseRoot);
        _maxEventCountPerPartition = options.Value.MaxEventCountPerPartition > 0
            ? options.Value.MaxEventCountPerPartition
            : throw new ArgumentOutOfRangeException(
                nameof(options), "MaxEventCountPerPartition must be positive.");
        Directory.CreateDirectory(_root);
        Discover();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (DatabaseEntry entry in _databases.Values)
        {
            await entry.DisposeAsync().ConfigureAwait(continueOnCapturedContext: false);
        }

        _gate.Dispose();
    }

    async Task<JsonEventStore> IDatabaseStoreProvider.GetStoreAsync(
        string database,
        CancellationToken cancellationToken)
    {
        return (await GetAsync(database, cancellationToken).ConfigureAwait(continueOnCapturedContext: false)).Store;
    }

    string IDatabaseStoreProvider.GetDirectory(string database)
    {
        return Get(database).Directory;
    }

    void IDatabaseStoreProvider.ReportFault(string database, Exception exception)
    {
        Get(database).MarkFault(exception);
    }

    public IReadOnlyList<DatabaseEntry> List()
    {
        return _databases.Values.OrderBy(x => x.Name, StringComparer.Ordinal).ToArray();
    }

    public DatabaseEntry Get(string name)
    {
        string normalized = NormalizeName(name);
        return _databases.TryGetValue(normalized, out DatabaseEntry? entry)
            ? entry
            : throw new KeyNotFoundException($"Database '{name}' was not found.");
    }

    public async Task<DatabaseEntry> CreateAsync(string name, CancellationToken cancellationToken)
    {
        string path = ResolvePath(name);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        try
        {
            if (_databases.ContainsKey(name) || Directory.Exists(path))
            {
                throw new InvalidOperationException($"Database '{name}' already exists.");
            }

            JsonEventStore store = await JsonEventStore.CreateAsync(CreateOptions(path), cancellationToken)
                .ConfigureAwait(continueOnCapturedContext: false);
            DatabaseEntry entry = new(
                NormalizeName(name), path, _maxEventCountPerPartition, store, new CatalogDocument());
            try
            {
                await entry.SaveCatalogAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
                _databases.Add(entry.Name, entry);
                return entry;
            }
            catch
            {
                await entry.DisposeAsync().ConfigureAwait(continueOnCapturedContext: false);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<DatabaseEntry> GetAsync(string name, CancellationToken cancellationToken)
    {
        DatabaseEntry entry = Get(name);
        await entry.OpenAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        return entry;
    }

    private string ResolvePath(string name)
    {
        string normalized = NormalizeName(name);
        string path =
            Path.GetFullPath(Path.Combine(_root, normalized.Replace(oldChar: '/', Path.DirectorySeparatorChar)));
        string prefix = _root.EndsWith(Path.DirectorySeparatorChar) ? _root : _root + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The database path must be below DatabaseRoot.", nameof(name));
        }

        return path;
    }

    public void BeginDrain()
    {
        foreach (DatabaseEntry entry in _databases.Values)
        {
            entry.BeginDrain();
        }
    }

    private void Discover()
    {
        IEnumerable<string> directories = Directory
            .EnumerateFiles(_root, "database_v1.json", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(_root, "store_partition_*_v1.json", SearchOption.AllDirectories))
            .Select(Path.GetDirectoryName)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (string directory in directories)
        {
            string name = Path.GetRelativePath(_root, directory).Replace(Path.DirectorySeparatorChar, newChar: '/');
            _databases.TryAdd(name, new DatabaseEntry(name, directory, _maxEventCountPerPartition));
        }
    }

    private DatabaseOptions CreateOptions(string path)
    {
        return new DatabaseOptions(path) { MaxEventCountPerPartition = _maxEventCountPerPartition };
    }

    private static string NormalizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || Path.IsPathRooted(name))
        {
            throw new ArgumentException("A relative database name is required.", nameof(name));
        }

        string normalized = name.Replace(oldChar: '\\', newChar: '/').Trim(trimChar: '/');
        if (normalized.Split(separator: '/').Any(x => x is "" or "." or ".."))
        {
            throw new ArgumentException("The database name contains an invalid path segment.", nameof(name));
        }

        return normalized;
    }

    internal static string Fingerprint(string value)
    {
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    internal static string CatalogPath(string directory)
    {
        return Path.Combine(directory, CatalogFileName);
    }
}