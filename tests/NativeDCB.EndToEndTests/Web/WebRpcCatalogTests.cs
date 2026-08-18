using NativeDCB.Web.Grpc.Discovery;

namespace NativeDCB.EndToEndTests.Web;

public sealed class WebRpcCatalogTests
{
    [Fact]
    public void WebConsoleWrapsEveryMethodDiscoveredFromTheProtocol()
    {
        Assert.Equal(expected: 31, RpcCatalog.Services.Sum(service => service.Methods.Count));
        Assert.Empty(RpcCatalog.MissingConsoleMethods);
    }
}