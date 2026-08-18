using NativeDCB.Web.Services;

namespace NativeDCB.EndToEndTests;

public sealed class WebRpcCatalogTests
{
    [Fact]
    public void WebConsoleWrapsEveryMethodDiscoveredFromTheProtocol()
    {
        Assert.Equal(expected: 29, RpcCatalog.Services.Sum(service => service.Methods.Count));
        Assert.Empty(RpcCatalog.MissingConsoleMethods);
    }
}