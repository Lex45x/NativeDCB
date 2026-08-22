namespace NativeDCB.SystemBenchmarks.Scenarios.Strategies;

internal sealed class CompletePurchaseScenario : BenchmarkScenario
{
    public CompletePurchaseScenario() : base("complete-purchase",
        "Correlated customer/cart/order/payment/shipment/delivery workflow.", ScheduleMode.CorrelatedWorkflow,
        ScheduleMode.CorrelatedWorkflow, ScheduleMode.ClosedLoop, ScheduleMode.OpenLoop)
    { }

    public override Task<OperationResult> ExecuteAsync(Workloads workloads, string database, long sequence,
        bool measure, CancellationToken cancellationToken) =>
        workloads.CompletePurchaseAsync(database, sequence, measure, cancellationToken);
}