namespace NativeDCB.Web.Grpc;

public interface INativeDcbConsole
{
    Task<string> ListDatabasesAsync(CancellationToken cancellationToken = default);
    Task<string> CreateDatabaseAsync(string database, CancellationToken cancellationToken = default);
    Task<string> GetDatabaseInfoAsync(string database, CancellationToken cancellationToken = default);
    Task<string> GetHealthAsync(CancellationToken cancellationToken = default);
    Task<string> GetCapabilitiesAsync(CancellationToken cancellationToken = default);
    Task<string> GetHeadAsync(string database, CancellationToken cancellationToken = default);

    Task<string> RegisterEventSchemaAsync(string database, string name, string schemaJson, bool allowIncompatible,
        CancellationToken cancellationToken = default);

    Task<string> RegisterCommandSchemaAsync(string database, string name, string schemaJson, bool allowIncompatible,
        CancellationToken cancellationToken = default);

    Task<string> RemoveSchemaAsync(string database, string name, string kind,
        CancellationToken cancellationToken = default);

    Task<string> GetSchemaAsync(string database, string name, string kind,
        CancellationToken cancellationToken = default);

    Task<string> ListSchemasAsync(string database, string kind, CancellationToken cancellationToken = default);

    Task<string> RegisterHandlerAsync(string database, string name, string commandType, string ndlSource,
        string planJson, bool allowIncompatible, CancellationToken cancellationToken = default);

    Task<string> RemoveHandlerAsync(string database, string name, CancellationToken cancellationToken = default);

    Task<string> GetHandlerAsync(string database, string name, bool includePlanJson, bool generateNdl,
        CancellationToken cancellationToken = default);

    Task<string> ListHandlersAsync(string database, CancellationToken cancellationToken = default);

    Task<string> ValidateNdlAsync(string database, string ndlSource, string transientSchemasJson,
        CancellationToken cancellationToken = default);

    Task<string> ExecuteHandlerAsync(string database, string handlerName, string commandJson, string commandId,
        CancellationToken cancellationToken = default);

    Task<string> PrepareDecisionAsync(string database, string handlerName, string commandJson, string commandId,
        CancellationToken cancellationToken = default);

    Task<string> CompleteDecisionAsync(string database, string modelSignature, string proposedEventsJson,
        CancellationToken cancellationToken = default);

    Task<string> GetEventsByCommandIdAsync(string database, string commandId,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<string> ReadEventsByRangeAsync(string database, long afterEventId, long? throughEventId,
        uint? limit, bool follow, CancellationToken cancellationToken = default);

    IAsyncEnumerable<string> ReadEventsByQueryAsync(string database, string eventTypes, string keys,
        long afterEventId, long? throughEventId, uint? limit, bool committedScan,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<string> ReadEventsByTypeAndKeysAsync(string database, string eventType, string keys,
        long afterEventId, long? throughEventId, uint? limit, bool committedScan,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<string> SubscribeEventsAsync(string database, long afterEventId, string eventTypes,
        string queryKeys, string subscriptionKeys, CancellationToken cancellationToken = default);

    IAsyncEnumerable<string> ExecuteStatementAsync(string database, string ndlSource, bool allowIncompatible,
        CancellationToken cancellationToken = default);

    Task<string> ExplainStatementAsync(string database, string ndlSource,
        CancellationToken cancellationToken = default);

    Task<string> ListPartitionsAsync(string database, CancellationToken cancellationToken = default);
    Task<string> ListIndexesAsync(string database, CancellationToken cancellationToken = default);

    Task<string> GetStateFileStatusAsync(string database, uint partitionNumber,
        CancellationToken cancellationToken = default);

    Task<string> RequestIndexRebuildAsync(string database, string eventType, string keys,
        CancellationToken cancellationToken = default);

    Task<string> RequestStateRebuildAsync(string database, uint partitionNumber,
        CancellationToken cancellationToken = default);
}