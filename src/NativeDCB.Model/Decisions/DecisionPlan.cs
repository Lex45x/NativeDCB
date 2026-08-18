using NativeDCB.Model.Decisions.Evaluation;

namespace NativeDCB.Model.Decisions;

public sealed record DecisionPlan(
    string Name,
    string CommandSchema,
    string CommandAlias,
    IReadOnlyList<PlanInclude> Includes,
    IReadOnlyList<PlanEvaluationStep> Evaluation,
    IReadOnlyList<PlanEmission> Emissions,
    DecisionPlanFingerprints Fingerprints);