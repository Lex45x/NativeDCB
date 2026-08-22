using Microsoft.AspNetCore.Authentication;
// ReSharper disable once RedundantUsingDirective -- Required by command-line builds.
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace NativeDCB.Server.Security;

internal static class NativeDcbAuthenticationExtensions
{
    private const string DispatcherScheme = "NativeDCB";

    public static void AddNativeDcbAuthentication(this WebApplicationBuilder builder)
    {
        IConfigurationSection section = builder.Configuration.GetSection("Authentication");
        NativeDcbAuthenticationOptions configured = section.Get<NativeDcbAuthenticationOptions>() ?? new();

        builder.Services.AddOptions<NativeDcbAuthenticationOptions>()
            .Bind(section)
            .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<NativeDcbAuthenticationOptions>,
            NativeDcbAuthenticationOptionsValidator>();

        AuthenticationBuilder authentication = builder.Services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = DispatcherScheme;
                options.DefaultChallengeScheme = DispatcherScheme;
            })
            .AddPolicyScheme(DispatcherScheme, DispatcherScheme, options =>
            {
                options.ForwardDefaultSelector = SelectScheme;
            })
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
                AuthenticationProviderNames.ApiKey, _ => { })
            .AddScheme<AuthenticationSchemeOptions, DisabledAuthenticationHandler>(
                AuthenticationProviderNames.Disabled, _ => { });

        authentication.AddJwtBearer(AuthenticationProviderNames.JwtBearer, options =>
        {
            options.Authority = configured.JwtBearer.Authority;
            options.Audience = configured.JwtBearer.Audience;
            options.RequireHttpsMetadata = configured.JwtBearer.RequireHttpsMetadata;
            options.MapInboundClaims = false;
        });

        builder.Services.AddAuthorization(options =>
        {
            options.FallbackPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build();
        });
        builder.Services.AddSingleton<ApiKeyStore>();
        builder.Services.AddHostedService<ApiKeyBootstrapService>();
    }

    private static string SelectScheme(HttpContext context)
    {
        NativeDcbAuthenticationOptions configured = context.RequestServices
            .GetRequiredService<IOptions<NativeDcbAuthenticationOptions>>().Value;
        if (configured.HasProvider(AuthenticationProviderNames.Disabled))
        {
            return AuthenticationProviderNames.Disabled;
        }

        string authorization = context.Request.Headers.Authorization.ToString();
        if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) &&
            configured.HasProvider(AuthenticationProviderNames.JwtBearer))
        {
            return AuthenticationProviderNames.JwtBearer;
        }

        if (authorization.StartsWith("ApiKey ", StringComparison.OrdinalIgnoreCase) &&
            configured.HasProvider(AuthenticationProviderNames.ApiKey))
        {
            return AuthenticationProviderNames.ApiKey;
        }

        return configured.HasProvider(AuthenticationProviderNames.ApiKey)
            ? AuthenticationProviderNames.ApiKey
            : AuthenticationProviderNames.JwtBearer;
    }

    internal static string? Validate(NativeDcbAuthenticationOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Providers))
        {
            return "Authentication requires at least one provider.";
        }

        string[] providers = options.ProviderNames();

        string[] known =
        [
            AuthenticationProviderNames.ApiKey,
            AuthenticationProviderNames.Disabled,
            AuthenticationProviderNames.JwtBearer
        ];
        if (providers.Any(provider => !known.Contains(provider, StringComparer.OrdinalIgnoreCase)))
        {
            return "Authentication contains an unknown provider.";
        }

        if (options.HasProvider(AuthenticationProviderNames.Disabled))
        {
            if (providers.Length != 1)
            {
                return "Disabled authentication must be the sole provider.";
            }

            return null;
        }

        if (options.HasProvider(AuthenticationProviderNames.JwtBearer) &&
            (string.IsNullOrWhiteSpace(options.JwtBearer.Authority) ||
             string.IsNullOrWhiteSpace(options.JwtBearer.Audience)))
        {
            return "JWT bearer authentication requires Authority and Audience.";
        }

        if (options.HasProvider(AuthenticationProviderNames.ApiKey) &&
            string.IsNullOrWhiteSpace(options.ApiKeys.StorePath))
        {
            return "API-key authentication requires StorePath.";
        }

        if (options.HasProvider(AuthenticationProviderNames.ApiKey) &&
            !options.ApiKeys.BootstrapOnEmptyStore &&
            !options.HasProvider(AuthenticationProviderNames.JwtBearer))
        {
            return "API-key bootstrap can be disabled only when JWT bearer authentication is enabled.";
        }

        return null;
    }
}

internal sealed class NativeDcbAuthenticationOptionsValidator : IValidateOptions<NativeDcbAuthenticationOptions>
{
    public ValidateOptionsResult Validate(string? name, NativeDcbAuthenticationOptions options)
    {
        string? error = NativeDcbAuthenticationExtensions.Validate(options);
        return error is null ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(error);
    }
}