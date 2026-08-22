namespace NativeDCB.Server.Security;

internal sealed record ApiKeyCatalog(string Schema, ApiKeyRecord[] Keys)
{
    public const string CurrentSchema = "native-dcb-api-keys-v1";

    public static ApiKeyCatalog Empty { get; } = new(CurrentSchema, []);
}

internal sealed record ApiKeyRecord(
    string KeyId,
    string Label,
    string SecretHash,
    string[] Permissions,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? ExpiresUtc,
    DateTimeOffset? RevokedUtc,
    bool Bootstrap);

internal sealed record CreatedApiKey(string Credential, ApiKeyRecord Record);