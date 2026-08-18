namespace NativeDCB.Web.Grpc.Discovery;

public sealed record RpcMethodMetadata(
    string Name,
    string FullName,
    string InputType,
    string OutputType,
    bool ClientStreaming,
    bool ServerStreaming);