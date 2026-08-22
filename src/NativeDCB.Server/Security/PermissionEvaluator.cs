using System.Security.Claims;

using NativeDCB.Actors.Storage;

namespace NativeDCB.Server.Security;

internal static class PermissionEvaluator
{
    public const string PermissionClaim = "native-dcb-permission";

    public static bool HasPermission(ClaimsPrincipal principal, PermissionGrant required)
    {
        return Grants(principal).Any(grant => grant.Covers(required));
    }

    public static bool CanDelegate(ClaimsPrincipal principal, IEnumerable<PermissionGrant> requested)
    {
        PermissionGrant[] available = Grants(principal).ToArray();
        return requested.All(permission => available.Any(grant => grant.Covers(permission)));
    }

    public static IEnumerable<PermissionGrant> Grants(ClaimsPrincipal principal)
    {
        foreach (Claim claim in principal.Claims)
        {
            if (claim.Type == PermissionClaim &&
                claim.Subject?.AuthenticationType is AuthenticationProviderNames.ApiKey or
                    AuthenticationProviderNames.Disabled)
            {
                if (PermissionGrant.TryParse(claim.Value, out PermissionGrant permission))
                {
                    yield return permission;
                }

            }

            if (claim.Type == PermissionClaim)
            {
                continue;
            }

            if (claim.Type is not ("scope" or "scp"))
            {
                continue;
            }

            foreach (string value in claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (PermissionGrant.TryParse(value, out PermissionGrant permission))
                {
                    yield return permission;
                }
            }
        }
    }

    public static PermissionGrant Grpc(string method)
    {
        string[] parts = method.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            throw new InvalidOperationException($"Unknown gRPC method path '{method}'.");
        }

        return new PermissionGrant("grpc", parts[0], parts[1]);
    }

    public static PermissionGrant Handler(string database, string handler, string action)
    {
        return new PermissionGrant(
            "handler",
            $"{Encode(ActorStoragePath.NormalizeDatabaseName(database))}/{Encode(handler)}",
            action);
    }

    public static PermissionGrant HandlerList(string database)
    {
        return new PermissionGrant(
            "handler",
            $"{Encode(ActorStoragePath.NormalizeDatabaseName(database))}/*",
            "list");
    }

    public static PermissionGrant ApiKey(string keyId, string action)
    {
        return new PermissionGrant("apikey", keyId == "*" ? "*" : Encode(keyId), action);
    }

    private static string Encode(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return Uri.EscapeDataString(value).Replace("*", "%2A", StringComparison.Ordinal);
    }
}