using Grpc.Net.Client;
using Grpc.Net.Client.Web;

using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

using NativeDCB.Web.Components;
using NativeDCB.Web.Grpc;
using NativeDCB.Web.Security;

WebAssemblyHostBuilder builder = WebAssemblyHostBuilder.CreateDefault(args);

builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddOidcAuthentication(options =>
{
    builder.Configuration.GetSection("Oidc").Bind(options.ProviderOptions);
    options.ProviderOptions.ResponseType = "code";
});
builder.Services.AddScoped<NativeDcbAuthorizationMessageHandler>();
builder.Services.AddScoped<GrpcChannel>(services =>
{
    IConfiguration configuration = services.GetRequiredService<IConfiguration>();
    string? address = configuration["NativeDCB:ServerAddress"];
    if (string.IsNullOrWhiteSpace(address))
    {
        throw new InvalidOperationException("NativeDCB:ServerAddress must be configured.");
    }

    string[] scopes = configuration.GetSection("NativeDCB:Scopes").Get<string[]>() ?? [];
    NativeDcbAuthorizationMessageHandler authorization =
        services.GetRequiredService<NativeDcbAuthorizationMessageHandler>();
    HttpMessageHandler handler = authorization.Configure(
        address,
        scopes,
        new GrpcWebHandler(GrpcWebMode.GrpcWeb, new HttpClientHandler()));
    return GrpcChannel.ForAddress(address, new GrpcChannelOptions { HttpHandler = handler });
});
builder.Services.AddScoped<INativeDcbConsole, NativeDcbConsole>();

await builder.Build().RunAsync();
