using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace NativeDCB.Actors.Observability;

internal static class ActorTelemetry
{
    public static readonly ActivitySource Activities = new("NativeDCB.Actors");
    private static readonly Meter Meter = new("NativeDCB.Actors");
    public static readonly Counter<long> Decisions = Meter.CreateCounter<long>("nativedcb.decisions");
    public static readonly Histogram<double> DecisionDuration =
        Meter.CreateHistogram<double>("nativedcb.decision.duration", unit: "s");
    public static readonly Counter<long> DecisionRetries = Meter.CreateCounter<long>("nativedcb.decision.retries");
    public static readonly Counter<long> Queries = Meter.CreateCounter<long>("nativedcb.queries");
    public static readonly Histogram<double> QueryDuration =
        Meter.CreateHistogram<double>("nativedcb.query.duration", unit: "s");
    public static readonly Histogram<long> QueryEvents = Meter.CreateHistogram<long>("nativedcb.query.events");
    public static readonly UpDownCounter<long> ActiveSubscriptions =
        Meter.CreateUpDownCounter<long>("nativedcb.subscriptions.active");
    public static readonly Counter<long> SubscriptionEvents =
        Meter.CreateCounter<long>("nativedcb.subscription.events");
    public static readonly Counter<long> MaintenanceOperations =
        Meter.CreateCounter<long>("nativedcb.maintenance.operations");
    public static readonly Histogram<double> MaintenanceDuration =
        Meter.CreateHistogram<double>("nativedcb.maintenance.duration", unit: "s");
    public static readonly Counter<long> DatabaseTransitions =
        Meter.CreateCounter<long>("nativedcb.database.transitions");
}