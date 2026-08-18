namespace NativeDCB.Engine.Actors;

public enum DecisionResultOutcome
{
    Committed,
    AlreadyCommitted,
    Rejected,
    Failed,
    Unavailable,
    DataLoss
}