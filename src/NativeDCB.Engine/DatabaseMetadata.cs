namespace NativeDCB.Engine;

internal sealed record DatabaseMetadata(
    int FormatVersion,
    Guid StoreId,
    DateTimeOffset CreatedUtc);