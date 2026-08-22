using BenchmarkDotNet.Attributes;

using NativeDCB.Model.Decisions;
using NativeDCB.Sdk.Decisions.Authoring;
using NativeDCB.Sdk.Schemas;

namespace NativeDCB.MicroBenchmarks.Decisions;

[MemoryDiagnoser]
public class SdkDecisionCompilationBenchmarks
{
    private Func<object> _construct = null!;
    private Func<DecisionPlan> _compile = null!;

    [Params(
        "Baseline[I=1,K=1,R=1,Q=1,E=1]",
        "Includes[I=4,K=1,R=1,Q=1,E=1]",
        "Keys[I=1,K=2,R=1,Q=1,E=1]",
        "Reducer[I=1,K=1,R=4,Q=1,E=1]",
        "Emissions[I=1,K=1,R=1,Q=1,E=2]")]
    public string Shape { get; set; } = null!;

    [GlobalSetup]
    public void Setup()
    {
        if (Shape.StartsWith("Includes", StringComparison.Ordinal))
        {
            SetFixture(() => BuildModel1(includeCount: 4, keyCount: 1, emissionCount: 1));
        }
        else if (Shape.StartsWith("Keys", StringComparison.Ordinal))
        {
            SetFixture(() => BuildModel1(includeCount: 1, keyCount: 2, emissionCount: 1));
        }
        else if (Shape.StartsWith("Reducer", StringComparison.Ordinal))
        {
            SetFixture(BuildModel4);
        }
        else if (Shape.StartsWith("Emissions", StringComparison.Ordinal))
        {
            SetFixture(() => BuildModel1(includeCount: 1, keyCount: 1, emissionCount: 2));
        }
        else
        {
            SetFixture(() => BuildModel1(includeCount: 1, keyCount: 1, emissionCount: 1));
        }
    }

    [Benchmark]
    public object ConstructFluentDefinition() => _construct();

    [Benchmark]
    public DecisionPlan CompilePlan() => _compile();

    private void SetFixture(Func<DecisionDefinition<BenchmarkCommand>> construct)
    {
        DecisionDefinition<BenchmarkCommand> definition = construct();
        _construct = construct;
        _compile = () => definition.Compile("BenchmarkDecision");
    }

    private static DecisionDefinition<BenchmarkCommand> BuildModel1(
        int includeCount,
        int keyCount,
        int emissionCount)
    {
        BenchmarkCommand command = new("entity-1", "tenant-1");
        DecisionModelEventSpec<BenchmarkCommand, EmptyDecisionModel, BenchmarkEvent, Model1> first =
            Decision.WithDecisionModel(command)
                .Include<BenchmarkEvent, Model1>((_, @event) => new Model1(@event.Value));
        DecisionModelBuilder<BenchmarkCommand, Model1> builder = keyCount == 1
            ? first.Where(@event => @event.Id == command.Id)
            : first.Where(@event => @event.Id == command.Id && @event.Tenant == command.Tenant);
        for (int include = 1; include < includeCount; include++)
        {
            builder = builder
                .Include<BenchmarkEvent, Model1>((_, @event) => new Model1(@event.Value))
                .Where(@event => @event.Id == command.Id);
        }

        EvaluatedDecision<BenchmarkCommand, Model1, bool> evaluated =
            builder.Evaluate((model, _) => model.Value >= 0);
        return emissionCount == 1
            ? evaluated.Decide((accepted, input) => accepted
                ? Decision.Accept(new BenchmarkOutput(input.Id))
                : Decision.Reject("rejected"))
            : evaluated.Decide((accepted, input) => accepted
                ? Decision.Accept(
                    new BenchmarkOutput(input.Id),
                    new BenchmarkAuditOutput(input.Id, input.Tenant))
                : Decision.Reject("rejected"));
    }

    private static DecisionDefinition<BenchmarkCommand> BuildModel4()
    {
        BenchmarkCommand command = new("entity-1", "tenant-1");
        return Decision.WithDecisionModel(command)
            .Include<BenchmarkEvent, Model4>((_, @event) =>
                new Model4(@event.Value, @event.Value + 1, @event.Value + 2, @event.Value + 3))
            .Where(@event => @event.Id == command.Id)
            .Evaluate((model, _) => model.A >= 0)
            .Decide((accepted, input) => accepted
                ? Decision.Accept(new BenchmarkOutput(input.Id))
                : Decision.Reject("rejected"));
    }

    [CommandType("benchmark-command")]
    public sealed record BenchmarkCommand(string Id, string Tenant);

    [EventType("benchmark-event")]
    public sealed record BenchmarkEvent(
        [property: ConsistencyKey("entity")] string Id,
        [property: ConsistencyKey("tenant")] string Tenant,
        long Value);

    [EventType("benchmark-output")]
    public sealed record BenchmarkOutput([property: ConsistencyKey("entity")] string Id);

    [EventType("benchmark-audit-output")]
    public sealed record BenchmarkAuditOutput(
        [property: ConsistencyKey("entity")] string Id,
        [property: ConsistencyKey("tenant")] string Tenant);

    public sealed record Model1(long Value);

    public sealed record Model4(long A, long B, long C, long D);
}
