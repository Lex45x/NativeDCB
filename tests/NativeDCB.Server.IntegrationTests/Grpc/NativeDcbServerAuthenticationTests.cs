using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

using Grpc.Core;
using Grpc.Net.Client;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

using NativeDCB.Actors.Decisions.Remote;
using NativeDCB.Actors.Messages;
using NativeDCB.Actors.Storage;
using NativeDCB.Protocol.V1;
using NativeDCB.Server.Security;

namespace NativeDCB.Server.IntegrationTests.Grpc;

public sealed partial class NativeDcbServerTests
{
    private const string JwtAudience = "nativedcb-tests";
    private const string JwtIssuer = "https://issuer.native-dcb.test";
    private const string JwtSigningSecret = "native-dcb-test-signing-key-32-bytes-minimum";

    [Fact]
    public async Task Bootstrap_recovery_revokes_lost_key_and_displays_a_new_credential_once()
    {
        string root = Path.Combine(Path.GetTempPath(), "NativeDCB.Server.AuthTests", Guid.NewGuid().ToString("N"));
        ApiKeyStore store = new(
            Options.Create(new NativeDcbAuthenticationOptions()),
            Options.Create(new ActorStorageOptions { DatabaseRoot = root }),
            TimeProvider.System);
        CreatedApiKey original = Assert.IsType<CreatedApiKey>(
            await store.InitializeAsync(CancellationToken.None));
        CreatedApiKey recovered = await store.RecoverBootstrapAsync(CancellationToken.None);

        Assert.NotEqual(original.Credential, recovered.Credential);
        Assert.Null(store.Authenticate(original.Credential));
        Assert.NotNull(store.Authenticate(recovered.Credential));
        IReadOnlyList<ApiKeyRecord> keys = await store.ListAsync(CancellationToken.None);
        Assert.Equal(expected: 2, keys.Count);
        Assert.NotNull(keys.Single(key => key.KeyId == original.Record.KeyId).RevokedUtc);
    }

    [Fact]
    public async Task Jwt_bearer_authentication_enforces_exact_grpc_permissions()
    {
        await using ServerFactory factory = new(authenticationDisabled: false, jwtAuthentication: true);
        using GrpcChannel channel = GrpcChannel.ForAddress(
            "http://localhost",
            new GrpcChannelOptions { HttpHandler = factory.Server.CreateHandler() });
        DatabaseService.DatabaseServiceClient databases = new(channel);

        Metadata authorizedHeaders = BearerHeaders(Token(
            "grpc:nativedcb.v1.DatabaseService:GetCapabilities"));
        GetCapabilitiesResponse capabilities = await databases.GetCapabilitiesAsync(
            new GetCapabilitiesRequest(), authorizedHeaders);
        Assert.Equal("v1", capabilities.ProtocolVersion);

        RpcException denied = await Assert.ThrowsAsync<RpcException>(async () =>
            await databases.ListDatabasesAsync(new ListDatabasesRequest(), authorizedHeaders));
        Assert.Equal(StatusCode.PermissionDenied, denied.StatusCode);

        RpcException unauthenticated = await Assert.ThrowsAsync<RpcException>(async () =>
            await databases.GetCapabilitiesAsync(new GetCapabilitiesRequest(), BearerHeaders("invalid")));
        Assert.Equal(StatusCode.Unauthenticated, unauthenticated.StatusCode);

        RpcException internalClaimDenied = await Assert.ThrowsAsync<RpcException>(async () =>
            await databases.GetCapabilitiesAsync(
                new GetCapabilitiesRequest(),
                BearerHeaders(Token(
                [
                    new Claim(
                        PermissionEvaluator.PermissionClaim,
                        "grpc:nativedcb.v1.DatabaseService:GetCapabilities")
                ]))));
        Assert.Equal(StatusCode.PermissionDenied, internalClaimDenied.StatusCode);
    }

    [Fact]
    public async Task Handler_permissions_are_database_action_and_name_specific()
    {
        string root = Path.Combine(Path.GetTempPath(), "NativeDCB.Server.AuthTests", Guid.NewGuid().ToString("N"));
        CreatedApiKey bootstrap = await CreateBootstrapAsync(root);
        await using ServerFactory factory = new(root, authenticationDisabled: false);
        using GrpcChannel channel = GrpcChannel.ForAddress(
            "http://localhost",
            new GrpcChannelOptions { HttpHandler = factory.Server.CreateHandler() });
        AuthenticationService.AuthenticationServiceClient authentication = new(channel);
        CatalogService.CatalogServiceClient catalog = new(channel);

        CreateApiKeyRequest create = new() { Label = "handler-reader" };
        create.Permissions.Add("grpc:nativedcb.v1.CatalogService:GetHandler");
        create.Permissions.Add("handler:school/Allowed:read");
        CreateApiKeyResponse reader = await authentication.CreateApiKeyAsync(
            create, Headers(bootstrap.Credential));

        RpcException authorizedMissing = await Assert.ThrowsAsync<RpcException>(async () =>
            await catalog.GetHandlerAsync(new GetHandlerRequest
            {
                Database = "school",
                HandlerName = "Allowed"
            }, Headers(reader.ApiKey)));
        Assert.Equal(StatusCode.NotFound, authorizedMissing.StatusCode);

        RpcException wrongHandler = await Assert.ThrowsAsync<RpcException>(async () =>
            await catalog.GetHandlerAsync(new GetHandlerRequest
            {
                Database = "school",
                HandlerName = "Other"
            }, Headers(reader.ApiKey)));
        Assert.Equal(StatusCode.PermissionDenied, wrongHandler.StatusCode);

        RpcException wrongDatabase = await Assert.ThrowsAsync<RpcException>(async () =>
            await catalog.GetHandlerAsync(new GetHandlerRequest
            {
                Database = "other",
                HandlerName = "Allowed"
            }, Headers(reader.ApiKey)));
        Assert.Equal(StatusCode.PermissionDenied, wrongDatabase.StatusCode);
    }

    [Fact]
    public async Task Execute_statement_requires_register_permission_for_every_parsed_handler()
    {
        string root = Path.Combine(Path.GetTempPath(), "NativeDCB.Server.AuthTests", Guid.NewGuid().ToString("N"));
        CreatedApiKey bootstrap = await CreateBootstrapAsync(root);
        await using ServerFactory factory = new(root, authenticationDisabled: false);
        using GrpcChannel channel = GrpcChannel.ForAddress(
            "http://localhost",
            new GrpcChannelOptions { HttpHandler = factory.Server.CreateHandler() });
        AuthenticationService.AuthenticationServiceClient authentication = new(channel);
        StatementService.StatementServiceClient statements = new(channel);

        CreateApiKeyRequest create = new() { Label = "statement-publisher" };
        create.Permissions.Add("grpc:nativedcb.v1.StatementService:ExecuteStatement");
        create.Permissions.Add("handler:school/DefineCourse:register");
        CreateApiKeyResponse publisher = await authentication.CreateApiKeyAsync(
            create, Headers(bootstrap.Credential));

        using AsyncServerStreamingCall<StatementResult> denied = statements.ExecuteStatement(
            new ExecuteStatementRequest
            {
                Database = "school",
                NdlSource = CreateCourseNdl.Replace("DefineCourse", "OtherHandler", StringComparison.Ordinal)
            }, Headers(publisher.ApiKey));
        RpcException exception = await Assert.ThrowsAsync<RpcException>(async () =>
            await denied.ResponseStream.MoveNext(CancellationToken.None));
        Assert.Equal(StatusCode.PermissionDenied, exception.StatusCode);
    }

    [Fact]
    public async Task Complete_decision_uses_handler_from_signed_capability()
    {
        string root = Path.Combine(Path.GetTempPath(), "NativeDCB.Server.AuthTests", Guid.NewGuid().ToString("N"));
        CreatedApiKey bootstrap = await CreateBootstrapAsync(root);
        await using ServerFactory factory = new(root, authenticationDisabled: false);
        using GrpcChannel channel = GrpcChannel.ForAddress(
            "http://localhost",
            new GrpcChannelOptions { HttpHandler = factory.Server.CreateHandler() });
        AuthenticationService.AuthenticationServiceClient authentication = new(channel);
        CommandService.CommandServiceClient commands = new(channel);

        CreateApiKeyRequest create = new() { Label = "wrong-completer" };
        create.Permissions.Add("grpc:nativedcb.v1.CommandService:CompleteDecision");
        create.Permissions.Add("handler:school/OtherHandler:complete");
        CreateApiKeyResponse completer = await authentication.CreateApiKeyAsync(
            create, Headers(bootstrap.Credential));

        DateTimeOffset now = new(year: 2026, month: 1, day: 1, hour: 0, minute: 0, second: 0, TimeSpan.Zero);
        MutableTimeProvider clock = new(now);
        RemoteDecisionOptions options = new()
        {
            ActiveKeyId = "test",
            SigningKeys = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["test"] = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8="
            },
            Lifetime = TimeSpan.FromMinutes(10)
        };
        RemoteDecisionTokenProtector protector = new(Options.Create(options), clock);
        byte[] signature = protector.Protect(new RemoteDecisionClaimsMessage(
            "school",
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Command",
            "AllowedHandler",
            "plan",
            HandlerVersion: 1,
            "command-hash",
            "model-hash",
            ObservedHead: 0,
            new EventQueryMessage([]),
            [],
            ["Event"],
            now,
            now.AddMinutes(10),
            RandomNumberGenerator.GetBytes(16)));

        RpcException denied = await Assert.ThrowsAsync<RpcException>(async () =>
            await commands.CompleteDecisionAsync(new CompleteDecisionRequest
            {
                Database = "school",
                ModelSignature = Google.Protobuf.ByteString.CopyFrom(signature)
            }, Headers(completer.ApiKey)));
        Assert.Equal(StatusCode.PermissionDenied, denied.StatusCode);
    }

    [Fact]
    public async Task Bootstrap_api_key_creates_scoped_key_and_is_atomically_revoked()
    {
        string root = Path.Combine(Path.GetTempPath(), "NativeDCB.Server.AuthTests", Guid.NewGuid().ToString("N"));
        NativeDcbAuthenticationOptions authenticationOptions = new();
        ActorStorageOptions storageOptions = new() { DatabaseRoot = root };
        ApiKeyStore bootstrapStore = new(
            Options.Create(authenticationOptions), Options.Create(storageOptions), TimeProvider.System);
        CreatedApiKey bootstrap = Assert.IsType<CreatedApiKey>(
            await bootstrapStore.InitializeAsync(CancellationToken.None));

        await using ServerFactory factory = new(root, authenticationDisabled: false);
        using GrpcChannel channel = GrpcChannel.ForAddress(
            "http://localhost",
            new GrpcChannelOptions { HttpHandler = factory.Server.CreateHandler() });
        DatabaseService.DatabaseServiceClient databases = new(channel);
        AuthenticationService.AuthenticationServiceClient authentication = new(channel);

        using HttpClient http = factory.CreateClient();
        HttpResponseMessage bootstrapReadiness = await http.GetAsync("/health/ready");
        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, bootstrapReadiness.StatusCode);

        RpcException unauthenticated = await Assert.ThrowsAsync<RpcException>(async () =>
            await databases.GetCapabilitiesAsync(new GetCapabilitiesRequest()));
        Assert.Equal(StatusCode.Unauthenticated, unauthenticated.StatusCode);

        GetCapabilitiesResponse capabilities = await databases.GetCapabilitiesAsync(
            new GetCapabilitiesRequest(), Headers(bootstrap.Credential));
        Assert.Equal("v1", capabilities.ProtocolVersion);

        CreateApiKeyRequest createAdmin = new() { Label = "administrator" };
        createAdmin.Permissions.Add("*:*:*");
        CreateApiKeyResponse admin = await authentication.CreateApiKeyAsync(
            createAdmin, Headers(bootstrap.Credential));
        Assert.StartsWith("ndcb1_", admin.ApiKey, StringComparison.Ordinal);
        Assert.False(admin.Key.Bootstrap);

        RpcException revokedBootstrap = await Assert.ThrowsAsync<RpcException>(async () =>
            await databases.GetCapabilitiesAsync(new GetCapabilitiesRequest(), Headers(bootstrap.Credential)));
        Assert.Equal(StatusCode.Unauthenticated, revokedBootstrap.StatusCode);

        CreateApiKeyRequest createReader = new() { Label = "capabilities-reader" };
        createReader.Permissions.Add("grpc:nativedcb.v1.DatabaseService:GetCapabilities");
        CreateApiKeyResponse reader = await authentication.CreateApiKeyAsync(
            createReader, Headers(admin.ApiKey));

        GetCapabilitiesResponse authorized = await databases.GetCapabilitiesAsync(
            new GetCapabilitiesRequest(), Headers(reader.ApiKey));
        Assert.Equal("v1", authorized.ProtocolVersion);

        RpcException denied = await Assert.ThrowsAsync<RpcException>(async () =>
            await databases.ListDatabasesAsync(new ListDatabasesRequest(), Headers(reader.ApiKey)));
        Assert.Equal(StatusCode.PermissionDenied, denied.StatusCode);

        ListApiKeysResponse listed = await authentication.ListApiKeysAsync(
            new ListApiKeysRequest(), Headers(admin.ApiKey));
        Assert.Equal(expected: 3, listed.Keys.Count);
        Assert.Contains(listed.Keys, key => key.KeyId == reader.Key.KeyId && !key.Bootstrap);

        RevokeApiKeyResponse revoked = await authentication.RevokeApiKeyAsync(
            new RevokeApiKeyRequest { KeyId = reader.Key.KeyId }, Headers(admin.ApiKey));
        Assert.NotNull(revoked.Key.RevokedUtc);
        RpcException revokedReader = await Assert.ThrowsAsync<RpcException>(async () =>
            await databases.GetCapabilitiesAsync(new GetCapabilitiesRequest(), Headers(reader.ApiKey)));
        Assert.Equal(StatusCode.Unauthenticated, revokedReader.StatusCode);

        CreateApiKeyRequest createDelegator = new() { Label = "key-creator" };
        createDelegator.Permissions.Add("grpc:nativedcb.v1.AuthenticationService:CreateApiKey");
        createDelegator.Permissions.Add("apikey:*:create");
        CreateApiKeyResponse delegator = await authentication.CreateApiKeyAsync(
            createDelegator, Headers(admin.ApiKey));
        CreateApiKeyRequest escalate = new() { Label = "escalated" };
        escalate.Permissions.Add("grpc:nativedcb.v1.DatabaseService:ListDatabases");
        RpcException escalationDenied = await Assert.ThrowsAsync<RpcException>(async () =>
            await authentication.CreateApiKeyAsync(escalate, Headers(delegator.ApiKey)));
        Assert.Equal(StatusCode.PermissionDenied, escalationDenied.StatusCode);

        string catalog = await File.ReadAllTextAsync(Path.Combine(root, ".security", "api_keys_v1.json"));
        Assert.DoesNotContain(bootstrap.Credential, catalog, StringComparison.Ordinal);
        Assert.DoesNotContain(admin.ApiKey, catalog, StringComparison.Ordinal);
        Assert.DoesNotContain(reader.ApiKey, catalog, StringComparison.Ordinal);
        Assert.DoesNotContain(delegator.ApiKey, catalog, StringComparison.Ordinal);
        Assert.Contains("secretHash", catalog, StringComparison.Ordinal);

        HttpResponseMessage readiness = await http.GetAsync("/health/ready");
        Assert.True(readiness.IsSuccessStatusCode);
    }

    [Theory]
    [InlineData("grpc:nativedcb.v1.CommandService:ExecuteHandler", "grpc:nativedcb.v1.CommandService:ExecuteHandler", true)]
    [InlineData("grpc:nativedcb.v1.CommandService:*", "grpc:nativedcb.v1.CommandService:ExecuteHandler", true)]
    [InlineData("grpc:*:*", "grpc:nativedcb.v1.CommandService:ExecuteHandler", true)]
    [InlineData("*:*:*", "handler:commerce/PublishProduct:execute", true)]
    [InlineData("handler:commerce/*:execute", "handler:commerce/PublishProduct:execute", true)]
    [InlineData("handler:*/PublishProduct:execute", "handler:commerce/PublishProduct:execute", true)]
    [InlineData("handler:other/*:execute", "handler:commerce/PublishProduct:execute", false)]
    [InlineData("grpc:nativedcb.v1.CommandService:PrepareDecision", "grpc:nativedcb.v1.CommandService:ExecuteHandler", false)]
    public void Permission_grants_match_exact_segments_and_wildcards(
        string available,
        string requested,
        bool expected)
    {
        Assert.True(PermissionGrant.TryParse(available, out PermissionGrant grant));
        Assert.True(PermissionGrant.TryParse(requested, out PermissionGrant permission));
        Assert.Equal(expected, grant.Covers(permission));
    }

    [Theory]
    [InlineData("")]
    [InlineData("grpc:service")]
    [InlineData("grpc:service:action:extra")]
    [InlineData("grpc:serv*ice:action")]
    [InlineData("handler:database/handler/extra:execute")]
    public void Permission_grants_reject_malformed_values(string value)
    {
        Assert.False(PermissionGrant.TryParse(value, out _));
    }

    private static Metadata Headers(string apiKey)
    {
        return new Metadata { { "authorization", $"ApiKey {apiKey}" } };
    }

    private static Metadata BearerHeaders(string token)
    {
        return new Metadata { { "authorization", $"Bearer {token}" } };
    }

    private static string Token(string permissions)
    {
        return Token([new Claim("scope", permissions)]);
    }

    private static string Token(IEnumerable<Claim> claims)
    {
        SymmetricSecurityKey key = new(Encoding.UTF8.GetBytes(JwtSigningSecret));
        JwtSecurityToken token = new(
            JwtIssuer,
            JwtAudience,
            claims,
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(10),
            new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static void ConfigureTestJwt(IServiceCollection services)
    {
        services.PostConfigure<JwtBearerOptions>(AuthenticationProviderNames.JwtBearer, options =>
        {
            SymmetricSecurityKey key = new(Encoding.UTF8.GetBytes(JwtSigningSecret));
            OpenIdConnectConfiguration configuration = new() { Issuer = JwtIssuer };
            configuration.SigningKeys.Add(key);
            options.Authority = null;
            options.Configuration = configuration;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidIssuer = JwtIssuer,
                ValidAudience = JwtAudience,
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateIssuerSigningKey = true,
                ValidateLifetime = true,
                IssuerSigningKey = key
            };
        });
    }

    private static async Task<CreatedApiKey> CreateBootstrapAsync(string root)
    {
        ApiKeyStore store = new(
            Options.Create(new NativeDcbAuthenticationOptions()),
            Options.Create(new ActorStorageOptions { DatabaseRoot = root }),
            TimeProvider.System);
        return Assert.IsType<CreatedApiKey>(await store.InitializeAsync(CancellationToken.None));
    }
}