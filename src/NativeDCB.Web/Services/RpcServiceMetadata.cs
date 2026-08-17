namespace NativeDCB.Web.Services;

public sealed record RpcServiceMetadata(int Ordinal, string Name, string FullName,
    IReadOnlyList<RpcMethodMetadata> Methods);