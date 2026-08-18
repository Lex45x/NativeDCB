using System.Text.Json;

using NativeDCB.Engine;
using NativeDCB.Model;

namespace NativeDCB.Server.Storage;

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