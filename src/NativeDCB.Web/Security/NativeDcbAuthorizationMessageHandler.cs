using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;

namespace NativeDCB.Web.Security;

internal sealed class NativeDcbAuthorizationMessageHandler(
    IAccessTokenProvider provider,
    NavigationManager navigation) : AuthorizationMessageHandler(provider, navigation)
{
    public HttpMessageHandler Configure(
        string serverAddress,
        IEnumerable<string> scopes,
        HttpMessageHandler innerHandler)
    {
        ConfigureHandler([serverAddress], scopes);
        InnerHandler = innerHandler;
        return this;
    }
}