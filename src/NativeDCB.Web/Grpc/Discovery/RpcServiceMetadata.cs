namespace NativeDCB.Web.Grpc.Discovery;

public sealed record RpcServiceMetadata(
    int Ordinal,
    string Name,
    string FullName,
    IReadOnlyList<RpcMethodMetadata> Methods);