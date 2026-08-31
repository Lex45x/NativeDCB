using System.Diagnostics.Metrics;

namespace NativeDCB.Server.Observability;

internal static class ServerTelemetry
{
    private static readonly Meter Meter = new("NativeDCB.Server");
    public static readonly Counter<long> AuthorizationDenials =
        Meter.CreateCounter<long>("nativedcb.authorization.denials");
    public static readonly Counter<long> AuditFailures = Meter.CreateCounter<long>("nativedcb.audit.failures");
}