using NativeDCB.Actors.Contracts;
using NativeDCB.Actors.Messages;
using NativeDCB.Actors.Storage;
using NativeDCB.Engine.Storage.EventLog;

namespace NativeDCB.Actors.Grains;

// ReSharper disable once UnusedType.Global -- Orleans activates grains by interface at runtime.
public sealed class DatabaseDirectoryGrain(ActorStoragePath storage, IGrainFactory grains)
    : Grain, IDatabaseDirectoryGrain
{
    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        storage.EnsureRootDirectory();
        return base.OnActivateAsync(cancellationToken);
    }

    public async Task<ListDatabasesActorResponse> ListAsync(
        ListDatabasesActorRequest request,
        GrainCancellationToken cancellationToken)
    {
        List<DatabaseSummaryMessage> databases = new();
        foreach (string database in storage.EnumerateDatabases())
        {
            cancellationToken.CancellationToken.ThrowIfCancellationRequested();
            MainStatusMessage status = await Main(database).GetStatusAsync(cancellationToken)
                .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
            databases.Add(new DatabaseSummaryMessage(
                database,
                status.Status,
                status.Head,
                status.ReadAvailable,
                status.WriteAvailable,
                status.LastFault));
        }

        return new ListDatabasesActorResponse(databases.ToArray());
    }

    public async Task<CreateDatabaseActorResponse> CreateAsync(
        CreateDatabaseActorRequest request,
        GrainCancellationToken cancellationToken)
    {
        string database;
        string directory;
        try
        {
            database = ActorStoragePath.NormalizeDatabaseName(request.Database);
            directory = storage.GetNewDatabaseDirectory(database);
        }
        catch (ArgumentException exception)
        {
            return new CreateDatabaseActorResponse(null, Error(
                DatabaseDirectoryErrorKind.InvalidArgument, exception.Message));
        }

        if (Directory.Exists(directory))
        {
            return new CreateDatabaseActorResponse(null, Error(
                DatabaseDirectoryErrorKind.AlreadyExists, $"Database '{request.Database}' already exists."));
        }

        try
        {
            await Main(database).CreateAsync(cancellationToken)
                .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
            ActorDatabaseInfoMessage info = await SnapshotAsync(database, cancellationToken)
                .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
            return new CreateDatabaseActorResponse(info);
        }
        catch (InvalidOperationException exception)
        {
            return new CreateDatabaseActorResponse(null, Error(
                DatabaseDirectoryErrorKind.AlreadyExists, exception.Message));
        }
        catch (InvalidDataException exception)
        {
            return new CreateDatabaseActorResponse(null, Error(
                DatabaseDirectoryErrorKind.DataLoss, exception.Message));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                                   EventStoreUnavailableException)
        {
            return new CreateDatabaseActorResponse(null, Error(
                DatabaseDirectoryErrorKind.Unavailable, exception.GetBaseException().Message));
        }
    }

    public async Task<GetDatabaseInfoActorResponse> GetInfoAsync(
        GetDatabaseInfoActorRequest request,
        GrainCancellationToken cancellationToken)
    {
        if (!TryFind(request.Database, out string database, out DatabaseDirectoryErrorMessage? error))
        {
            return new GetDatabaseInfoActorResponse(null, error);
        }

        try
        {
            return new GetDatabaseInfoActorResponse(await SnapshotAsync(database, cancellationToken)
                .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext));
        }
        catch (InvalidDataException exception)
        {
            return new GetDatabaseInfoActorResponse(null, Error(
                DatabaseDirectoryErrorKind.DataLoss, exception.Message));
        }
    }

    public async Task<GetHealthActorResponse> GetHealthAsync(
        GetHealthActorRequest request,
        GrainCancellationToken cancellationToken)
    {
        List<DatabaseHealthMessage> databases = new();
        foreach (string database in storage.EnumerateDatabases())
        {
            cancellationToken.CancellationToken.ThrowIfCancellationRequested();
            MainStatusMessage status = await Main(database).GetStatusAsync(cancellationToken)
                .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
            databases.Add(new DatabaseHealthMessage(
                database,
                status.Status,
                Live: true,
                status.ReadAvailable,
                status.WriteAvailable,
                status.LastFault));
        }

        return new GetHealthActorResponse(Live: true, databases.ToArray());
    }

    public Task<GetCapabilitiesActorResponse> GetCapabilitiesAsync(
        GetCapabilitiesActorRequest request,
        GrainCancellationToken cancellationToken)
    {
        cancellationToken.CancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new GetCapabilitiesActorResponse(
            ProtocolVersion: "v1",
            NdlVersion: "v1",
            FileFormatVersions: ["1"],
            QueryFeatures: ["range", "type", "keys", "committed-scan"],
            SubscriptionsSupported: true,
            Limits:
            [
                new CapabilityLimitMessage(
                    "max_event_count_per_partition",
                    checked((ulong)storage.MaxEventCountPerPartition))
            ]));
    }

    public async Task<GetMainHeadActorResponse> GetMainHeadAsync(
        GetMainHeadActorRequest request,
        GrainCancellationToken cancellationToken)
    {
        if (!TryFind(request.Database, out string database, out DatabaseDirectoryErrorMessage? error))
        {
            return new GetMainHeadActorResponse(EventId: 0, error);
        }

        try
        {
            WriterStateMessage state = await Main(database).GetStateAsync(cancellationToken)
                .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
            return new GetMainHeadActorResponse(state.Head);
        }
        catch (InvalidDataException exception)
        {
            return new GetMainHeadActorResponse(0, Error(
                DatabaseDirectoryErrorKind.DataLoss, exception.Message));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                                   EventStoreUnavailableException)
        {
            return new GetMainHeadActorResponse(0, Error(
                DatabaseDirectoryErrorKind.Unavailable, exception.GetBaseException().Message));
        }
    }

    private async Task<ActorDatabaseInfoMessage> SnapshotAsync(
        string database,
        GrainCancellationToken cancellationToken)
    {
        MainStatusMessage main = await Main(database).GetStatusAsync(cancellationToken)
            .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
        SchemaSnapshotMessage schemas = await grains.GetGrain<ISchemaGrain>(database)
            .GetSnapshotAsync(cancellationToken)
            .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
        HandlerListMessage handlers = await grains.GetGrain<IHandlerGrain>(database)
            .ListAsync(cancellationToken)
            .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
        CatalogFingerprintMessage[] fingerprints = schemas.EventSchemas
            .Select(value => new CatalogFingerprintMessage("event-schema", value.Name, value.Fingerprint))
            .Concat(schemas.CommandSchemas.Select(value =>
                new CatalogFingerprintMessage("command-schema", value.Name, value.Fingerprint)))
            .Concat(handlers.Handlers.Select(value =>
                new CatalogFingerprintMessage("handler", value.Name, value.PlanFingerprint)))
            .ToArray();
        return new ActorDatabaseInfoMessage(
            database,
            main.Status,
            DatabaseVersion: "1",
            FileFormatVersion: "1",
            main.Head,
            main.ActivePartition,
            main.WriterLockOwned,
            fingerprints,
            main.ReadAvailable,
            main.WriteAvailable,
            main.LastFault);
    }

    private bool TryFind(
        string requested,
        out string database,
        out DatabaseDirectoryErrorMessage? error)
    {
        try
        {
            database = ActorStoragePath.NormalizeDatabaseName(requested);
        }
        catch (ArgumentException exception)
        {
            database = string.Empty;
            error = Error(DatabaseDirectoryErrorKind.InvalidArgument, exception.Message);
            return false;
        }

        if (!storage.EnumerateDatabases().Contains(database, StringComparer.OrdinalIgnoreCase))
        {
            error = Error(
                DatabaseDirectoryErrorKind.NotFound, $"Database '{requested}' was not found.");
            return false;
        }

        error = null;
        return true;
    }

    private IMainWriterGrain Main(string database)
    {
        return grains.GetGrain<IMainWriterGrain>(database);
    }

    private static DatabaseDirectoryErrorMessage Error(
        DatabaseDirectoryErrorKind kind,
        string message)
    {
        return new DatabaseDirectoryErrorMessage(kind, message);
    }
}