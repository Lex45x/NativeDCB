namespace NativeDCB.SystemBenchmarks.Scenarios.Strategies;

internal sealed class ReservationCheckoutScenario : BenchmarkScenario
{
    public ReservationCheckoutScenario() : base("reservation-checkout",
        "Reservation and checkout race for one cart.", ScheduleMode.SynchronizedBurst,
        ScheduleMode.ClosedLoop, ScheduleMode.OpenLoop, ScheduleMode.SynchronizedBurst)
    { }

    protected override async Task SetupScenarioAsync(BenchmarkRuntime runtime, CancellationToken cancellationToken)
    {
        runtime.DefaultMeasurementOperationLimit(runtime.FiniteCohortSize());
        await runtime.PrepareRaceSlotsAsync(runtime.MeasurementOperationLimit!.Value, measure: true,
            cancellationToken);
        await runtime.PrepareRaceSlotsAsync(runtime.DatabaseCount, measure: false, cancellationToken);
    }

    public override Task<OperationResult> ExecuteAsync(Workloads workloads, string database, long sequence,
        bool measure, CancellationToken cancellationToken) =>
        workloads.ReservationCheckoutAsync(sequence, measure, cancellationToken);

    protected override Task VerifyScenarioAsync(BenchmarkRuntime runtime, List<string> errors,
        CancellationToken cancellationToken) => runtime.VerifyReservationCheckoutAsync(errors, cancellationToken);
}