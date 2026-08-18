namespace NativeDCB.Engine.Actors.Messages;

[GenerateSerializer]
[Immutable]
public sealed record TransactionRequestMessage(
    [property: Id(id: 0)] string Database,
    [property: Id(id: 1)] Guid CommandId,
    [property: Id(id: 2)] string HandlerName,
    [property: Id(id: 3)] string CommandType,
    [property: Id(id: 4)] string NdlSource,
    [property: Id(id: 5)] string SourceFingerprint,
    [property: Id(id: 6)] string PlanFingerprint,
    [property: Id(id: 7)] string CommandJson,
    [property: Id(id: 8)] SchemaDocumentMessage[] EventSchemas,
    [property: Id(id: 9)] SchemaDocumentMessage? CommandSchema,
    [property: Id(id: 10)] string PlanJson);