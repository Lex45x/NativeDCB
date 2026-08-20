using NativeDCB.Model.Events;

namespace NativeDCB.Actors.Decisions.Execution;

internal sealed record DecisionExecution(
    DecisionOutcome Outcome,
    IReadOnlyList<SequencedEvent> Events,
    string? RejectionCode,
    string? RejectionMessage)
{
    public static DecisionExecution Committed(IReadOnlyList<SequencedEvent> events)
    {
        return new DecisionExecution(DecisionOutcome.Committed, events, RejectionCode: null, RejectionMessage: null);
    }

    public static DecisionExecution AlreadyCommitted(IReadOnlyList<SequencedEvent> events)
    {
        return new DecisionExecution(DecisionOutcome.AlreadyCommitted, events, RejectionCode: null,
            RejectionMessage: null);
    }

    public static DecisionExecution Reject(string code, string message)
    {
        return new DecisionExecution(DecisionOutcome.Rejected, [], code, message);
    }
}