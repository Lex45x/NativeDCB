using System.Text.Json;

using BenchmarkDotNet.Attributes;

using NativeDCB.Actors.Decisions.Execution;
using NativeDCB.MicroBenchmarks.Infrastructure;
using NativeDCB.Model.Decisions;
using NativeDCB.Model.Decisions.Evaluation;
using NativeDCB.Model.Decisions.Expressions;
using NativeDCB.Model.Events;

namespace NativeDCB.MicroBenchmarks.Decisions;

[MemoryDiagnoser]
public class DecisionModelBenchmarks
{
    public IEnumerable<ReplayCase> ReplayCases()
    {
        JsonElement command = JsonSerializer.SerializeToElement(new { Id = "entity-1" });
        foreach (int historySize in new[] { 1, 64, 1000 })
        foreach (int includes in new[] { 1, 4 })
        foreach (int assignments in new[] { 1, 4 })
        {
            DecisionPlan plan = ReplayPlan(includes, assignments);
            SequencedEvent[] events = Enumerable.Range(0, historySize)
                .Select(index => Event(index, $"event-{index % includes}"))
                .ToArray();
            yield return new ReplayCase(
                $"History={historySize},Includes={includes},Assignments={assignments}",
                plan,
                command,
                events);
        }
    }

    public IEnumerable<EvaluationCase> EvaluationCases()
    {
        JsonElement command = JsonSerializer.SerializeToElement(new { Id = "entity-1" });
        foreach (int modelProperties in new[] { 1, 16 })
        foreach (int requirements in new[] { 1, 4, 16 })
        foreach (int emissions in new[] { 1, 4 })
        foreach (string rejectionPosition in new[] { "Accepted", "First", "Last" })
        {
            Dictionary<string, object?> model = Enumerable.Range(0, modelProperties)
                .ToDictionary(index => $"value-{index}", index => (object?)(long)index, StringComparer.Ordinal);
            yield return new EvaluationCase(
                $"ModelProperties={modelProperties},Requirements={requirements},Emissions={emissions},Reject={rejectionPosition}",
                EvaluationPlan(requirements, emissions, rejectionPosition),
                command,
                model);
        }
    }

    [Benchmark]
    [ArgumentsSource(nameof(ReplayCases))]
    public Dictionary<string, object?> Replay(ReplayCase input) =>
        DecisionModelKernel.Replay(input.Plan, input.Command, input.Events, ensureInitialized: true);

    [Benchmark]
    [ArgumentsSource(nameof(EvaluationCases))]
    public object Evaluate(EvaluationCase input) =>
        DecisionModelKernel.Evaluate(input.Plan, input.Command, input.Model);

    private static DecisionPlan ReplayPlan(int includeCount, int assignmentCount)
    {
        PlanInclude[] includes = Enumerable.Range(0, includeCount)
            .Select(include => new PlanInclude(
                $"event-{include}",
                $"event{include}",
                Boolean(value: true),
                [],
                Enumerable.Range(0, assignmentCount)
                    .Select(assignment => new PlanAssignment(
                        $"value-{include}-{assignment}",
                        new PlanMemberExpression(new PlanSymbolExpression($"event{include}"), "Value")))
                    .ToArray()))
            .ToArray();
        return Plan(includes, [], []);
    }

    private static DecisionPlan EvaluationPlan(int requirementCount, int emissionCount, string rejectionPosition)
    {
        PlanEvaluationStep[] evaluation = Enumerable.Range(0, requirementCount)
            .Select(index => (PlanEvaluationStep)new PlanRequirement(
                Boolean(rejectionPosition switch
                {
                    "First" => index != 0,
                    "Last" => index != requirementCount - 1,
                    _ => true
                }),
                new PlanLiteralExpression(PlanLiteralKind.String, $"rejected-{index}")))
            .ToArray();
        PlanEmission[] emissions = Enumerable.Range(0, emissionCount)
            .Select(index => new PlanEmission(
                $"accepted-{index}",
                [new PlanAssignment(
                    "Id",
                    new PlanMemberExpression(new PlanSymbolExpression("command"), "Id"))]))
            .ToArray();
        return Plan([], evaluation, emissions);
    }

    private static DecisionPlan Plan(
        IReadOnlyList<PlanInclude> includes,
        IReadOnlyList<PlanEvaluationStep> evaluation,
        IReadOnlyList<PlanEmission> emissions) => new(
        "BenchmarkDecision",
        "BenchmarkCommand",
        "command",
        includes,
        evaluation,
        emissions,
        new DecisionPlanFingerprints("benchmark-v1", "fixed", new Dictionary<string, string>()));

    private static SequencedEvent Event(int index, string type) => new(
        index + 1L,
        type,
        JsonSerializer.SerializeToElement(new { Value = index }),
        [new EventKey("entity", "entity-1")],
        SchemaVersion: 1,
        BenchmarkFixtures.Timestamp,
        BenchmarkFixtures.GuidFor(index + 1),
        "benchmark-command");

    private static PlanLiteralExpression Boolean(bool value) =>
        new(PlanLiteralKind.Boolean, value ? "true" : "false");

    public sealed record ReplayCase(
        string Shape,
        DecisionPlan Plan,
        JsonElement Command,
        IReadOnlyList<SequencedEvent> Events)
    {
        public override string ToString() => Shape;
    }

    public sealed record EvaluationCase(
        string Shape,
        DecisionPlan Plan,
        JsonElement Command,
        Dictionary<string, object?> Model)
    {
        public override string ToString() => Shape;
    }
}
