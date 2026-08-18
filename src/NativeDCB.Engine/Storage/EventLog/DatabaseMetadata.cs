namespace NativeDCB.Engine.Storage.EventLog;

internal sealed record DatabaseMetadata(
    int FormatVersion,
    Guid StoreId,
    DateTimeOffset CreatedUtc);