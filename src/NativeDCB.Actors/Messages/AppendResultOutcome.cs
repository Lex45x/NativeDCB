namespace NativeDCB.Actors.Messages;

public enum AppendResultOutcome
{
    Committed,
    Conflict,
    AlreadyCommitted
}