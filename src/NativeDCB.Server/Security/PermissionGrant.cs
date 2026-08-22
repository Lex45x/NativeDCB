namespace NativeDCB.Server.Security;

internal readonly record struct PermissionGrant(string Group, string Resource, string Action)
{
    public const string Superuser = "*:*:*";

    public static bool TryParse(string value, out PermissionGrant grant)
    {
        grant = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string[] segments = value.Split(':');
        if (segments.Length != 3 || segments.Any(string.IsNullOrWhiteSpace))
        {
            return false;
        }

        grant = new PermissionGrant(segments[0], segments[1], segments[2]);
        return ValidSegment(grant.Group) && ValidResource(grant.Resource) && ValidSegment(grant.Action);
    }

    public bool Covers(PermissionGrant requested)
    {
        return CoversSegment(Group, requested.Group) &&
               CoversResource(Resource, requested.Resource) &&
               CoversSegment(Action, requested.Action);
    }

    public override string ToString()
    {
        return $"{Group}:{Resource}:{Action}";
    }

    private static bool CoversSegment(string available, string requested)
    {
        return available == "*" || string.Equals(available, requested, StringComparison.Ordinal);
    }

    private static bool CoversResource(string available, string requested)
    {
        if (available == "*" || string.Equals(available, requested, StringComparison.Ordinal))
        {
            return true;
        }

        string[] availableParts = available.Split('/');
        string[] requestedParts = requested.Split('/');
        return availableParts.Length == 2 && requestedParts.Length == 2 &&
               CoversSegment(availableParts[0], requestedParts[0]) &&
               CoversSegment(availableParts[1], requestedParts[1]);
    }

    private static bool ValidResource(string value)
    {
        string[] parts = value.Split('/');
        return parts.Length switch
        {
            1 => ValidSegment(parts[0]),
            2 => ValidSegment(parts[0]) && ValidSegment(parts[1]),
            _ => false
        };
    }

    private static bool ValidSegment(string value)
    {
        return value == "*" || value.All(character =>
            character is >= '!' and <= '~' && character is not ':' and not '/' and not '*');
    }
}