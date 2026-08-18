namespace NativeDCB.Engine.Actors.Messages;

public enum AppendResultOutcome
{
    Committed,
    Conflict,
    AlreadyCommitted
}