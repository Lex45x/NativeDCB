using System.Net;

using NativeDCB.Sdk.Client;

namespace NativeDCB.Sdk.Tests.Client;

public sealed class NativeDcbCredentialsTests
{
    [Fact]
    public async Task Authentication_handler_resolves_and_adds_credentials_for_each_request()
    {
        int calls = 0;
        NativeDcbCredentials credentials = NativeDcbCredentials.BearerToken(_ =>
            ValueTask.FromResult($"token-{++calls}"));
        CapturingHandler inner = new();
        using NativeDcbAuthenticationHandler handler = new(credentials, inner);
        using HttpMessageInvoker invoker = new(handler);

        await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Post, "https://localhost/one"),
            CancellationToken.None);
        await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Post, "https://localhost/two"),
            CancellationToken.None);

        Assert.Equal(["Bearer token-1", "Bearer token-2"], inner.Authorization);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<string> Authorization { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Authorization.Add(request.Headers.Authorization?.ToString() ?? string.Empty);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}