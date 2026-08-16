using Google.Protobuf.Reflection;

using NativeDCB.Protocol.V1;

namespace NativeDCB.Web.Services;

public sealed record RpcMethodMetadata(
    string Name,
    string FullName,
    string InputType,
    string OutputType,
    bool ClientStreaming,
    bool ServerStreaming);

public sealed record RpcServiceMetadata(int Ordinal, string Name, string FullName,
    IReadOnlyList<RpcMethodMetadata> Methods);

public static class RpcCatalog
{
    public static IReadOnlyList<RpcServiceMetadata> Services { get; } = Build();
    public static IReadOnlyList<RpcMethodMetadata> MissingConsoleMethods { get; } = FindMissingConsoleMethods();

    private static IReadOnlyList<RpcServiceMetadata> Build()
    {
        return NativedcbReflection.Descriptor.Services.Select((service, index) =>
        {
            string displayName = service.Name.EndsWith("Service", StringComparison.Ordinal)
                ? service.Name[..^"Service".Length]
                : service.Name;
            return new RpcServiceMetadata(
                index + 1,
                displayName,
                service.FullName,
                service.Methods.Select(method => Method(service, method)).ToArray());
        }).ToArray();
    }

    private static RpcMethodMetadata Method(
        Google.Protobuf.Reflection.ServiceDescriptor service,
        MethodDescriptor method)
    {
        return new RpcMethodMetadata(
            method.Name,
            $"{service.Name}.{method.Name}",
            method.InputType.FullName,
            method.OutputType.FullName,
            method.IsClientStreaming,
            method.IsServerStreaming);
    }

    private static IReadOnlyList<RpcMethodMetadata> FindMissingConsoleMethods()
    {
        HashSet<string> consoleMethods = typeof(INativeDcbConsole).GetMethods()
            .Select(method => method.Name.EndsWith("Async", StringComparison.Ordinal)
                ? method.Name[..^"Async".Length]
                : method.Name)
            .ToHashSet(StringComparer.Ordinal);
        return Services.SelectMany(service => service.Methods)
            .Where(method => !consoleMethods.Contains(method.Name))
            .ToArray();
    }
}