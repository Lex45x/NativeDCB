namespace NativeDCB.Engine.Actors.Messages;

public enum DecisionResultOutcome
{
    Committed,
    AlreadyCommitted,
    Rejected,
    Failed,
    Unavailable,
    DataLoss
}