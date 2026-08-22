namespace NativeDCB.Server.Security;

// These properties are populated by Microsoft.Extensions.Configuration at runtime.
// ReSharper disable AutoPropertyCanBeMadeGetOnly.Global
// ReSharper disable UnusedAutoPropertyAccessor.Global
internal static class AuthenticationProviderNames
{
    public const string ApiKey = "ApiKey";
    public const string Disabled = "Disabled";
    public const string JwtBearer = "JwtBearer";
}

internal sealed class NativeDcbAuthenticationOptions
{
    public string Providers { get; set; } = AuthenticationProviderNames.ApiKey;
    public NativeDcbApiKeyOptions ApiKeys { get; set; } = new();
    public NativeDcbJwtBearerOptions JwtBearer { get; set; } = new();

    public bool HasProvider(string provider)
    {
        return ProviderNames().Contains(provider, StringComparer.OrdinalIgnoreCase);
    }

    public string[] ProviderNames()
    {
        return Providers.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}

internal sealed class NativeDcbApiKeyOptions
{
    public string StorePath { get; set; } = Path.Combine(".security", "api_keys_v1.json");
    public bool BootstrapOnEmptyStore { get; set; } = true;
}

internal sealed class NativeDcbJwtBearerOptions
{
    public string? Authority { get; set; }
    public string? Audience { get; set; }
    public bool RequireHttpsMetadata { get; set; } = true;
}
