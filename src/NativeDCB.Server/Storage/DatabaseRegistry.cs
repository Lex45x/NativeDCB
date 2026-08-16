using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Options;

using NativeDCB.Engine;
using NativeDCB.Engine.Actors;
using NativeDCB.Model;

namespace NativeDCB.Server.Storage;

public sealed class ServerOptions
{
    // ReSharper disable once AutoPropertyCanBeMadeGetOnly.Global -- Set by the configuration binder.
    public string DatabaseRoot { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NativeDCB",
        "databases");

    // ReSharper disable once AutoPropertyCanBeMadeGetOnly.Global -- Set by the configuration binder.
    public int MaxEventCountPerPartition { get; set; } = 10_000;
}

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

public sealed class DatabaseEntry : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(initialCount: 1, maxCount: 1);
    private readonly int _maxEventCountPerPartition;
    private Exception? _lastFault;
    private JsonEventStore? _store;

    internal DatabaseEntry(
        string name,
        string directory,
        int maxEventCountPerPartition,
        JsonEventStore? store = null,
        CatalogDocument? catalog = null)
    {
        Name = name;
        Directory = directory;
        _maxEventCountPerPartition = maxEventCountPerPartition;
        _store = store;
        Status = store is null ? DatabaseStatus.Discovered : DatabaseStatus.Ready;
        Catalog = catalog ?? new CatalogDocument();
    }

    public string Name { get; }
    public string Directory { get; }
    public CatalogDocument Catalog { get; private set; }
    public DatabaseStatus Status { get; private set; }

    public string? LastFault => _lastFault?.Message ?? _store?.LastFault;
    internal SemaphoreSlim CatalogLock { get; } = new(initialCount: 1, maxCount: 1);
    public bool IsOpen => _store is not null;
    public bool ReadAvailable => File.Exists(Path.Combine(Directory, "store_partition_000001_v1.json"));
    public bool WriteAvailable => Status == DatabaseStatus.Ready && _store is { IsFaulted: false };
    public long Head => _store?.Head ?? 0;
    public int ActivePartition => _store?.ActivePartition ?? 0;
    public JsonEventStore Store => _store ?? throw new InvalidOperationException("The database is not open.");

    public async ValueTask DisposeAsync()
    {
        Status = DatabaseStatus.Draining;
        if (_store is not null)
        {
            await _store.DisposeAsync().ConfigureAwait(continueOnCapturedContext: false);
            _store = null;
        }

        _gate.Dispose();
        CatalogLock.Dispose();
        Status = DatabaseStatus.Stopped;
    }

    internal async Task OpenAsync(CancellationToken cancellationToken)
    {
        if (_store is not null)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        try
        {
            if (_store is not null)
            {
                return;
            }

            Status = DatabaseStatus.AcquiringLock;
            try
            {
                Status = DatabaseStatus.Recovering;
                _store = await JsonEventStore
                    .OpenAsync(
                        new DatabaseOptions(Directory) { MaxEventCountPerPartition = _maxEventCountPerPartition },
                        cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
                Catalog = await LoadCatalogAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
                _lastFault = null;
                Status = DatabaseStatus.Ready;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                MarkFault(exception);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveCatalogAsync(CancellationToken cancellationToken)
    {
        string path = DatabaseRegistry.CatalogPath(Directory);
        string temporary = path + ".tmp";
        await using (FileStream stream = new(
                         temporary, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 4096,
                         FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, Catalog, DatabaseRegistry.JsonOptions, cancellationToken)
                .ConfigureAwait(continueOnCapturedContext: false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, path, overwrite: true);
    }

    internal void MarkFault(Exception exception)
    {
        _lastFault = exception;
        Status = DatabaseStatus.Faulted;
    }

    internal void BeginDrain()
    {
        if (Status == DatabaseStatus.Ready)
        {
            Status = DatabaseStatus.Draining;
        }
    }

    private async Task<CatalogDocument> LoadCatalogAsync(CancellationToken cancellationToken)
    {
        string path = DatabaseRegistry.CatalogPath(Directory);
        if (!File.Exists(path))
        {
            CatalogDocument empty = new();
            Catalog = empty;
            await SaveCatalogAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
            return empty;
        }

        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        CatalogDocument? catalog = await JsonSerializer.DeserializeAsync<CatalogDocument>(
            stream, DatabaseRegistry.JsonOptions, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        return catalog is { FormatVersion: 1 }
            ? catalog
            : throw new InvalidDataException($"Catalog '{path}' is invalid.");
    }
}

public sealed class CatalogDocument
{
    public int FormatVersion { get; init; } = 1;
    public ConcurrentDictionary<string, SchemaCatalogEntry> EventSchemas { get; init; } = new(StringComparer.Ordinal);
    public ConcurrentDictionary<string, SchemaCatalogEntry> CommandSchemas { get; init; } = new(StringComparer.Ordinal);
    public ConcurrentDictionary<string, HandlerCatalogEntry> Handlers { get; init; } = new(StringComparer.Ordinal);
}

public sealed record SchemaCatalogEntry(string Name, string DocumentJson, string Fingerprint);

public sealed record HandlerCatalogEntry(
    string Name,
    string CommandType,
    string NdlSource,
    string SourceFingerprint,
    string PlanFingerprint,
    string PlanJson)
{
    public DecisionPlan ParsePlan()
    {
        return JsonSerializer.Deserialize<DecisionPlan>(
                   PlanJson, DatabaseRegistry.JsonOptions)
               ?? throw new InvalidDataException($"Stored handler plan '{Name}' is invalid.");
    }
}