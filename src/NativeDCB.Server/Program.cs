using NativeDCB.Engine.Actors;
using NativeDCB.Engine.Actors.Contracts;
using NativeDCB.Model;
using NativeDCB.Model.Databases;
using NativeDCB.Server.Catalog;
using NativeDCB.Server.Databases;
using NativeDCB.Server.Grpc;
using NativeDCB.Server.Grpc.Infrastructure;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.AddGrpc(options => options.Interceptors.Add<GrpcExceptionInterceptor>());
string[] grpcWebOrigins = builder.Configuration.GetSection("GrpcWeb:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options => options.AddPolicy("GrpcWeb", policy =>
{
    if (grpcWebOrigins.Length > 0)
    {
        policy.WithOrigins(grpcWebOrigins)
            .AllowAnyMethod()
            .AllowAnyHeader()
            .WithExposedHeaders(
                "grpc-status",
                "grpc-message",
                "grpc-encoding",
                "grpc-accept-encoding",
                "native-dcb-error-bin");
    }
}));
builder.Services.AddSingleton<GrpcExceptionInterceptor>();
builder.Services.Configure<ServerOptions>(builder.Configuration);
builder.Services.AddSingleton<DatabaseRegistry>();
builder.Services.AddSingleton<IDatabaseStoreProvider>(services => services.GetRequiredService<DatabaseRegistry>());
builder.Services.AddSingleton<IEventBatchValidator, CatalogEventBatchValidator>();
int siloPort = builder.Configuration.GetValue("Orleans:SiloPort", defaultValue: 11111);
int gatewayPort = builder.Configuration.GetValue("Orleans:GatewayPort", defaultValue: 30000);
builder.UseOrleans(silo => silo.UseLocalhostClustering(siloPort, gatewayPort));

WebApplication app = builder.Build();
DatabaseRegistry registry = app.Services.GetRequiredService<DatabaseRegistry>();
app.Lifetime.ApplicationStopping.Register(registry.BeginDrain);

app.UseGrpcWeb();
app.UseCors();

app.MapGrpcService<DatabaseGrpcService>().EnableGrpcWeb().RequireCors("GrpcWeb");
app.MapGrpcService<CatalogGrpcService>().EnableGrpcWeb().RequireCors("GrpcWeb");
app.MapGrpcService<EventGrpcService>().EnableGrpcWeb().RequireCors("GrpcWeb");
app.MapGrpcService<CommandGrpcService>().EnableGrpcWeb().RequireCors("GrpcWeb");
app.MapGrpcService<StatementGrpcService>().EnableGrpcWeb().RequireCors("GrpcWeb");
app.MapGrpcService<AdministrationGrpcService>().EnableGrpcWeb().RequireCors("GrpcWeb");
app.MapGet("/", () => "NativeDCB gRPC server");
app.MapGet("/health/live", () => Results.Ok(new { live = true }));
app.MapGet("/health/ready", () => registry.List().All(entry => entry.Status is
    DatabaseStatus.Ready or DatabaseStatus.Discovered)
    ? Results.Ok(new { ready = true })
    : Results.Json(new { ready = false }, statusCode: StatusCodes.Status503ServiceUnavailable));

app.Run();

// ReSharper disable once ClassNeverInstantiated.Global -- WebApplicationFactory discovers the ASP.NET entry point.
namespace NativeDCB.Server
{
    public class Program;
}