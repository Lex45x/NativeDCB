using Grpc.Core;

using NativeDCB.Engine.Actors;
using NativeDCB.Protocol.V1;
using NativeDCB.Server.Actors;
using NativeDCB.Server.Storage;

namespace NativeDCB.Server.Services;

public sealed class DatabaseGrpcService(DatabaseRegistry registry, IGrainFactory grains)
    : DatabaseService.DatabaseServiceBase
{
    public override Task<ListDatabasesResponse> ListDatabases(
        ListDatabasesRequest request,
        ServerCallContext context)
    {
        ListDatabasesResponse response = new();
        foreach (DatabaseEntry entry in registry.List())
        {
            DatabaseSummary summary = new()
            {
                Database = entry.Name,
                State = ProtocolMapper.ToDatabaseState(entry.Status),
                MainHead = entry.Head,
                ReadAvailable = entry.ReadAvailable,
                WriteAvailable = entry.WriteAvailable
            };
            if (entry.LastFault is { } fault)
            {
                summary.Fault = new ErrorDetail { Code = "DatabaseFaulted", Message = fault };
            }

            response.Databases.Add(summary);
        }

        return Task.FromResult(response);
    }

    public override async Task<CreateDatabaseResponse> CreateDatabase(
        CreateDatabaseRequest request,
        ServerCallContext context)
    {
        try
        {
            DatabaseEntry entry = await registry.CreateAsync(request.Database, context.CancellationToken)
                .ConfigureAwait(continueOnCapturedContext: false);
            WriterStateMessage state = await GetWriterStateAsync(entry.Name, context.CancellationToken)
                .ConfigureAwait(continueOnCapturedContext: false);
            return new CreateDatabaseResponse { Database = ProtocolMapper.ToDatabaseInfo(entry, state) };
        }
        catch (ArgumentException exception)
        {
            throw ProtocolMapper.InvalidArgument(exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            throw ProtocolMapper.AlreadyExists(exception.Message);
        }
    }

    public override Task<GetDatabaseInfoResponse> GetDatabaseInfo(
        GetDatabaseInfoRequest request,
        ServerCallContext context)
    {
        try
        {
            DatabaseEntry entry = registry.Get(request.Database);
            return Task.FromResult(new GetDatabaseInfoResponse { Database = ProtocolMapper.ToDatabaseInfo(entry) });
        }
        catch (ArgumentException exception)
        {
            throw ProtocolMapper.InvalidArgument(exception.Message);
        }
        catch (KeyNotFoundException exception)
        {
            throw ProtocolMapper.NotFound(exception.Message);
        }
    }

    public override Task<GetHealthResponse> GetHealth(GetHealthRequest request, ServerCallContext context)
    {
        GetHealthResponse response = new() { Live = true };
        foreach (DatabaseEntry entry in registry.List())
        {
            DatabaseHealth health = new()
            {
                Database = entry.Name,
                State = ProtocolMapper.ToDatabaseState(entry.Status),
                Live = true,
                ReadReady = entry.ReadAvailable,
                WriteReady = entry.WriteAvailable
            };
            if (entry.LastFault is { } fault)
            {
                health.Fault = new ErrorDetail { Code = "DatabaseFaulted", Message = fault };
            }

            response.Databases.Add(health);
        }

        return Task.FromResult(response);
    }

    public override Task<GetCapabilitiesResponse> GetCapabilities(
        GetCapabilitiesRequest request,
        ServerCallContext context)
    {
        GetCapabilitiesResponse response = new()
        {
            ProtocolVersion = "v1", NdlVersion = "v1", SubscriptionsSupported = true
        };
        response.FileFormatVersions.Add("1");
        response.QueryFeatures.AddRange(["range", "type", "keys", "committed-scan"]);
        response.Limits.Add("max_event_count_per_partition", value: 10_000);
        return Task.FromResult(response);
    }

    public override async Task<GetHeadResponse> GetHead(GetHeadRequest request, ServerCallContext context)
    {
        DatabaseEntry entry = await GetAsync(request.Database, context.CancellationToken)
            .ConfigureAwait(continueOnCapturedContext: false);
        WriterStateMessage state = await GetWriterStateAsync(entry.Name, context.CancellationToken)
            .ConfigureAwait(continueOnCapturedContext: false);
        return new GetHeadResponse { EventId = state.Head };
    }

    private async Task<WriterStateMessage> GetWriterStateAsync(
        string database,
        CancellationToken cancellationToken)
    {
        IMainWriterGrain writer = grains.GetGrain<IMainWriterGrain>(database);
        return await GrainCall.RunAsync(
            writer.GetStateAsync,
            cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
    }

    private async Task<DatabaseEntry> GetAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            return await registry.GetAsync(name, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        }
        catch (ArgumentException exception)
        {
            throw ProtocolMapper.InvalidArgument(exception.Message);
        }
        catch (KeyNotFoundException exception)
        {
            throw ProtocolMapper.NotFound(exception.Message);
        }
    }
}