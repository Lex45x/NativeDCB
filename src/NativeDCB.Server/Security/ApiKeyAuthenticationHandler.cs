using System.Security.Claims;
using System.Text.Encodings.Web;

using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace NativeDCB.Server.Security;

internal sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ApiKeyStore store) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string authorization = Request.Headers.Authorization.ToString();
        const string prefix = "ApiKey ";
        if (!authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        string credential = authorization[prefix.Length..].Trim();
        ApiKeyRecord? key = store.Authenticate(credential);
        if (key is null)
        {
            return Task.FromResult(AuthenticateResult.Fail("The API key is invalid."));
        }

        List<Claim> claims =
        [
            new(ClaimTypes.NameIdentifier, key.KeyId),
            new(ClaimTypes.Name, key.Label)
        ];
        claims.AddRange(key.Permissions.Select(permission =>
            new Claim(PermissionEvaluator.PermissionClaim, permission)));
        ClaimsIdentity identity = new(claims, Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}