using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace NativeDCB.Engine.Observability;

internal static class EngineTelemetry
{
    public static readonly ActivitySource Activities = new("NativeDCB.Engine");
    private static readonly Meter Meter = new("NativeDCB.Engine");
    public static readonly Counter<long> AuditRecords = Meter.CreateCounter<long>("nativedcb.audit.records");
    public static readonly Counter<long> AuditFailures = Meter.CreateCounter<long>("nativedcb.audit.failures");
    public static readonly Histogram<double> AuditAppendDuration =
        Meter.CreateHistogram<double>("nativedcb.audit.append.duration", unit: "s");
    public static readonly Counter<long> EventsCommitted = Meter.CreateCounter<long>("nativedcb.events.committed");
    public static readonly Histogram<double> AppendDuration =
        Meter.CreateHistogram<double>("nativedcb.append.duration", unit: "s");
}