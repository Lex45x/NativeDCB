using NativeDCB.Model.Decisions;
using NativeDCB.Model.Queries;

namespace NativeDCB.Actors.Decisions.Execution;

internal sealed record PreparedDecisionResult(
    DecisionPlan Plan,
    EventQuery Query,
    long ObservedHead,
    byte[] ModelJson,
    Dictionary<string, object?> StructuralModel);