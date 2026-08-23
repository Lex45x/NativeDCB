using Microsoft.Extensions.Options;

using NativeDCB.Actors.Audit;
using NativeDCB.Actors.Contracts;
using NativeDCB.Actors.Decisions.Remote;
using NativeDCB.Actors.Messages;
using NativeDCB.Actors.Storage;
using NativeDCB.Engine.Storage.Audit;
using NativeDCB.Model.Databases;
using NativeDCB.Server.Audit;
using NativeDCB.Server.Decisions.Transactions;
using NativeDCB.Server.Grpc;
using NativeDCB.Server.Grpc.Infrastructure;
using NativeDCB.Server.Observability;
using NativeDCB.Server.Security;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

if (args is ["auth", "recover-bootstrap-api-key"])
{
    NativeDcbAuthenticationOptions authentication =
        builder.Configuration.GetSection("Authentication").Get<NativeDcbAuthenticationOptions>() ?? new();
    ActorStorageOptions storage = builder.Configuration.Get<ActorStorageOptions>() ?? new();
    AuditOptions auditOptions = builder.Configuration.GetSection("Audit").Get<AuditOptions>() ?? new();
    ApiKeyStore recoveryStore = new(
        Options.Create(authentication), Options.Create(storage), TimeProvider.System);
    await using AuditJournal audit = new(
        storage.DatabaseRoot, auditOptions.MaxRecordCountPerPartition, TimeProvider.System);
    await audit.InitializeAsync(CancellationToken.None);
    Guid operationId = Guid.NewGuid();
    await audit.AppendAsync(new AuditRecordDraft(
        operationId,
        "attempt",
        "security",
        "system/auth/recover-bootstrap-api-key",
        AuthenticationScheme: "System",
        Subject: Environment.UserName), CancellationToken.None);
    CreatedApiKey recovered;
    try
    {
        recovered = await recoveryStore.RecoverBootstrapAsync(CancellationToken.None);
    }
    catch (Exception)
    {
        await audit.AppendAsync(new AuditRecordDraft(
            operationId,
            "outcome",
            "security",
            "system/auth/recover-bootstrap-api-key",
            AuthenticationScheme: "System",
            Subject: Environment.UserName,
            Outcome: "failed",
            Code: "unexpected"), CancellationToken.None);
        throw;
    }

    await audit.AppendAsync(new AuditRecordDraft(
        operationId,
        "outcome",
        "security",
        "system/auth/recover-bootstrap-api-key",
        AuthenticationScheme: "System",
        Subject: Environment.UserName,
        Outcome: "replaced",
        Resource: $"apikey:{recovered.Record.KeyId}"), CancellationToken.None);

    Console.Out.WriteLine("NativeDCB replacement bootstrap API key (shown once):");
    Console.Out.WriteLine(recovered.Credential);
    return;
}

builder.AddNativeDcbAuthentication();
builder.AddNativeDcbObservability();
builder.Services.AddGrpc(options =>
{
    options.Interceptors.Add<GrpcExceptionInterceptor>();
    options.Interceptors.Add<GrpcAuthorizationInterceptor>();
});
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
builder.Services.AddSingleton<GrpcAuthorizationInterceptor>();
builder.Services.Configure<ActorStorageOptions>(builder.Configuration);
builder.Services.Configure<AuditOptions>(builder.Configuration.GetSection("Audit"));
builder.Services.Configure<RemoteDecisionOptions>(builder.Configuration.GetSection("RemoteDecisions"));
builder.Services.AddSingleton<ActorStoragePath>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<RemoteDecisionTokenProtector>();
builder.Services.AddSingleton(serviceProvider =>
{
    ActorStorageOptions storage = serviceProvider.GetRequiredService<IOptions<ActorStorageOptions>>().Value;
    AuditOptions audit = serviceProvider.GetRequiredService<IOptions<AuditOptions>>().Value;
    return new AuditJournal(
        storage.DatabaseRoot,
        audit.MaxRecordCountPerPartition,
        serviceProvider.GetRequiredService<TimeProvider>());
});
builder.Services.AddHostedService<AuditJournalLifetimeService>();
int siloPort = builder.Configuration.GetValue("Orleans:SiloPort", defaultValue: 11111);
int gatewayPort = builder.Configuration.GetValue("Orleans:GatewayPort", defaultValue: 30000);
builder.UseOrleans(silo => silo.UseLocalhostClustering(siloPort, gatewayPort));

WebApplication app = builder.Build();

app.UseGrpcWeb();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

// The interceptor owns gRPC authorization so it can durably audit denials before returning them.
app.MapGrpcService<DatabaseGrpcService>().EnableGrpcWeb().RequireCors("GrpcWeb").AllowAnonymous();
app.MapGrpcService<CatalogGrpcService>().EnableGrpcWeb().RequireCors("GrpcWeb").AllowAnonymous();
app.MapGrpcService<EventGrpcService>().EnableGrpcWeb().RequireCors("GrpcWeb").AllowAnonymous();
app.MapGrpcService<CommandGrpcService>().EnableGrpcWeb().RequireCors("GrpcWeb").AllowAnonymous();
app.MapGrpcService<StatementGrpcService>().EnableGrpcWeb().RequireCors("GrpcWeb").AllowAnonymous();
app.MapGrpcService<AdministrationGrpcService>().EnableGrpcWeb().RequireCors("GrpcWeb").AllowAnonymous();
app.MapGrpcService<AuthenticationGrpcService>().EnableGrpcWeb().RequireCors("GrpcWeb").AllowAnonymous();
app.MapGrpcService<AuditGrpcService>().EnableGrpcWeb().RequireCors("GrpcWeb").AllowAnonymous();
app.MapGet("/", () => "NativeDCB gRPC server");
app.MapGet("/health/live", () => Results.Ok(new { live = true })).AllowAnonymous();
app.MapGet("/health/ready", async (
    IGrainFactory grains,
    ApiKeyStore apiKeys,
    CancellationToken cancellationToken) =>
{
    if (apiKeys.HasActiveBootstrap)
    {
        return Results.Json(new { ready = false }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    try
    {
        AuditStatusMessage audit = await GrainCall.RunAsync(
            _ => grains.GetGrain<IAuditGrain>(IAuditGrain.SingletonKey).GetStatusAsync(), cancellationToken);
        if (!audit.Ready)
        {
            return Results.Json(new { ready = false }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
    catch (Exception)
    {
        return Results.Json(new { ready = false }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    GetHealthActorResponse health = await GrainCall.RunAsync(
        token => grains.GetGrain<IDatabaseDirectoryGrain>(IDatabaseDirectoryGrain.SingletonKey)
            .GetHealthAsync(new GetHealthActorRequest(), token), cancellationToken);
    return health.Databases.All(database => database.Status is DatabaseStatus.Ready or DatabaseStatus.Discovered)
        ? Results.Ok(new { ready = true })
        : Results.Json(new { ready = false }, statusCode: StatusCodes.Status503ServiceUnavailable);
}).AllowAnonymous();

app.Run();

// ReSharper disable once ClassNeverInstantiated.Global -- WebApplicationFactory discovers the ASP.NET entry point.
namespace NativeDCB.Server
{
    public class Program;
}