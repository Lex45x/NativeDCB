using Google.Protobuf.WellKnownTypes;

using Grpc.Core;

using NativeDCB.Protocol.V1;
using NativeDCB.Server.Grpc.Infrastructure;
using NativeDCB.Server.Security;

namespace NativeDCB.Server.Grpc;

internal sealed class AuthenticationGrpcService(ApiKeyStore store) : AuthenticationService.AuthenticationServiceBase
{
    public override async Task<CreateApiKeyResponse> CreateApiKey(
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
        EnsureEnabled();
        ApiKeyRecord? revoked;
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
            : new RevokeApiKeyResponse { Key = ToInfo(revoked) };
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

    private void EnsureEnabled()
    {
        if (!store.Enabled)
        {
            throw ProtocolMapper.FailedPrecondition("The API-key authentication provider is not enabled.");
        }
    }
}