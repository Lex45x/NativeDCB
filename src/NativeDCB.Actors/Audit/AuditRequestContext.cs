using NativeDCB.Actors.Messages;

namespace NativeDCB.Actors.Audit;

public static class AuditRequestContext
{
    private const string ContextKey = "native-dcb-audit-context";

    public static AuditOperationContextMessage? Current =>
        RequestContext.Get(ContextKey) as AuditOperationContextMessage;

    public static object? Set(AuditOperationContextMessage context)
    {
        ArgumentNullException.ThrowIfNull(context);
        object? previous = RequestContext.Get(ContextKey);
        RequestContext.Set(ContextKey, context);
        return previous;
    }

    public static void Restore(object? previous)
    {
        if (previous is null)
        {
            RequestContext.Remove(ContextKey);
        }
        else
        {
            RequestContext.Set(ContextKey, previous);
        }
    }
}