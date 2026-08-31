using Google.Protobuf.WellKnownTypes;

using Grpc.Core;

using NativeDCB.Actors.Audit;
using NativeDCB.Actors.Contracts;
using NativeDCB.Actors.Messages;
using NativeDCB.Protocol.V1;
using NativeDCB.Server.Decisions.Transactions;
using NativeDCB.Server.Grpc.Infrastructure;
using NativeDCB.Server.Observability;
using NativeDCB.Server.Security;

namespace NativeDCB.Server.Grpc;

internal sealed class AuthenticationGrpcService(ApiKeyStore store, IGrainFactory grains)
    : AuthenticationService.AuthenticationServiceBase
{
    public override async Task<CreateApiKeyResponse> CreateApiKey(
        CreateApiKeyRequest request,
        ServerCallContext context)
    {
        CreateApiKeyResponse response;
        try
        {
            response = await CreateApiKeyCoreAsync(request, context).ConfigureAwait(false);
        }
        catch (RpcException exception)
        {
            await AuditOutcomeAsync("rejected", exception.StatusCode.ToString()).ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException)
        {
            await AuditOutcomeAsync("cancelled").ConfigureAwait(false);
            throw;
        }
        catch (Exception)
        {
            await AuditOutcomeAsync("failed", "unexpected").ConfigureAwait(false);
            throw;
        }

        await AuditOutcomeAsync("created", resource: $"apikey:{response.Key.KeyId}").ConfigureAwait(false);
        return response;
    }

    private async Task<CreateApiKeyResponse> CreateApiKeyCoreAsync(
        CreateApiKeyRequest request,
        ServerCallContext context)
    {
        EnsureEnabled();
        PermissionGrant[] permissions = request.Permissions.Select(value =>
                PermissionGrant.TryParse(value, out PermissionGrant permission)
                    ? permission
                    : throw ProtocolMapper.InvalidArgument($"Permission '{value}' is invalid."))
            .Distinct()
            .ToArray();
        if (permissions.Length == 0)
        {
            throw ProtocolMapper.InvalidArgument("At least one permission is required.");
        }

        if (!PermissionEvaluator.CanDelegate(context.GetHttpContext().User, permissions))
        {
            throw ProtocolMapper.PermissionDenied("The requested permissions exceed the caller's permissions.");
        }

        DateTimeOffset? expiresUtc = request.ExpiresUtc?.ToDateTimeOffset();
        CreatedApiKey created;
        try
        {
            created = await store.CreateAsync(
                    request.Label, permissions, expiresUtc, context.CancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ArgumentException or ArgumentOutOfRangeException)
        {
            throw ProtocolMapper.InvalidArgument(exception.Message);
        }

        return new CreateApiKeyResponse
        {
            ApiKey = created.Credential,
            Key = ToInfo(created.Record)
        };
    }

    public override async Task<ListApiKeysResponse> ListApiKeys(
        ListApiKeysRequest request,
        ServerCallContext context)
    {
        EnsureEnabled();
        IReadOnlyList<ApiKeyRecord> keys = await store.ListAsync(context.CancellationToken).ConfigureAwait(false);
        ListApiKeysResponse response = new();
        response.Keys.AddRange(keys.Select(ToInfo));
        return response;
    }

    public override async Task<RevokeApiKeyResponse> RevokeApiKey(
        RevokeApiKeyRequest request,
        ServerCallContext context)
    {
        (RevokeApiKeyResponse Response, bool AlreadyRevoked) result;
        try
        {
            result = await RevokeApiKeyCoreAsync(request, context).ConfigureAwait(false);
        }
        catch (RpcException exception)
        {
            await AuditOutcomeAsync(
                    exception.StatusCode == StatusCode.NotFound ? "not_found" : "rejected",
                    exception.StatusCode.ToString(),
                    $"apikey:{request.KeyId}")
                .ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException)
        {
            await AuditOutcomeAsync("cancelled", resource: $"apikey:{request.KeyId}").ConfigureAwait(false);
            throw;
        }
        catch (Exception)
        {
            await AuditOutcomeAsync("failed", "unexpected", $"apikey:{request.KeyId}").ConfigureAwait(false);
            throw;
        }

        await AuditOutcomeAsync(
                result.AlreadyRevoked ? "already_revoked" : "revoked",
                resource: $"apikey:{request.KeyId}")
            .ConfigureAwait(false);
        return result.Response;
    }

    private async Task<(RevokeApiKeyResponse Response, bool AlreadyRevoked)> RevokeApiKeyCoreAsync(
        RevokeApiKeyRequest request,
        ServerCallContext context)
    {
        EnsureEnabled();
        ApiKeyRevocation? revoked;
        try
        {
            revoked = await store.RevokeAsync(request.KeyId, context.CancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException exception)
        {
            throw ProtocolMapper.InvalidArgument(exception.Message);
        }

        return revoked is null
            ? throw ProtocolMapper.NotFound($"API key '{request.KeyId}' was not found.")
            : (new RevokeApiKeyResponse { Key = ToInfo(revoked.Record) }, revoked.AlreadyRevoked);
    }

    private static ApiKeyInfo ToInfo(ApiKeyRecord record)
    {
        ApiKeyInfo info = new()
        {
            KeyId = record.KeyId,
            Label = record.Label,
            CreatedUtc = Timestamp.FromDateTimeOffset(record.CreatedUtc),
            Bootstrap = record.Bootstrap
        };
        info.Permissions.AddRange(record.Permissions);
        if (record.ExpiresUtc is { } expires)
        {
            info.ExpiresUtc = Timestamp.FromDateTimeOffset(expires);
        }

        if (record.RevokedUtc is { } revoked)
        {
            info.RevokedUtc = Timestamp.FromDateTimeOffset(revoked);
        }

        return info;
    }

    private async Task AuditOutcomeAsync(string outcome, string? code = null, string? resource = null)
    {
        AuditOperationContextMessage? audit = AuditRequestContext.Current;
        if (audit is null)
        {
            return;
        }

        try
        {
            await GrainCall.RunAsync(
                    token => grains.GetGrain<IAuditGrain>(IAuditGrain.SingletonKey).AppendAsync(
                        new AppendAuditRecordMessage(
                            audit.OperationId,
                            "outcome",
                            audit.Category,
                            audit.Operation,
                            audit.AuthenticationScheme,
                            audit.Subject,
                            audit.Issuer,
                            audit.Database,
                            resource ?? audit.Resource,
                            outcome,
                            code,
                            TraceId: audit.TraceId),
                        token),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            ServerTelemetry.AuditFailures.Add(1,
                new KeyValuePair<string, object?>("operation", audit.Operation),
                new KeyValuePair<string, object?>("fault.kind", "outcome_persistence"));
            throw ProtocolMapper.Unavailable("The audit journal is unavailable.");
        }
    }

    private void EnsureEnabled()
    {
        if (!store.Enabled)
        {
            throw ProtocolMapper.FailedPrecondition("The API-key authentication provider is not enabled.");
        }
    }
}