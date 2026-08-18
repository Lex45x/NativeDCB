namespace NativeDCB.Model.Events.Appending;

public enum AppendOutcome
{
    Committed,
    Conflict,
    AlreadyCommitted
}