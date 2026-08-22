using System.Net.Http.Headers;

namespace NativeDCB.Sdk.Client;

public sealed class NativeDcbCredentials
{
    private readonly Func<CancellationToken, ValueTask<string>> _credentialProvider;

    private NativeDcbCredentials(
        string scheme,
        Func<CancellationToken, ValueTask<string>> credentialProvider)
    {
        Scheme = scheme;
        _credentialProvider = credentialProvider;
    }

    internal string Scheme { get; }

    public static NativeDcbCredentials ApiKey(string apiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        return ApiKey(_ => ValueTask.FromResult(apiKey));
    }

    public static NativeDcbCredentials ApiKey(Func<CancellationToken, ValueTask<string>> apiKeyProvider)
    {
        ArgumentNullException.ThrowIfNull(apiKeyProvider);
        return new NativeDcbCredentials("ApiKey", apiKeyProvider);
    }

    public static NativeDcbCredentials BearerToken(string accessToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        return BearerToken(_ => ValueTask.FromResult(accessToken));
    }

    public static NativeDcbCredentials BearerToken(
        Func<CancellationToken, ValueTask<string>> accessTokenProvider)
    {
        ArgumentNullException.ThrowIfNull(accessTokenProvider);
        return new NativeDcbCredentials("Bearer", accessTokenProvider);
    }

    internal async ValueTask<AuthenticationHeaderValue> GetHeaderAsync(CancellationToken cancellationToken)
    {
        string credential = await _credentialProvider(cancellationToken).ConfigureAwait(false);
        ArgumentException.ThrowIfNullOrWhiteSpace(credential);
        return new AuthenticationHeaderValue(Scheme, credential);
    }
}

internal sealed class NativeDcbAuthenticationHandler(
    NativeDcbCredentials credentials,
    HttpMessageHandler innerHandler) : DelegatingHandler(innerHandler)
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        request.Headers.Authorization = await credentials.GetHeaderAsync(cancellationToken).ConfigureAwait(false);
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}