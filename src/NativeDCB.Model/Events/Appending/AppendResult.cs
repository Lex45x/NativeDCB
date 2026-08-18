namespace NativeDCB.Model.Events.Appending;

public sealed record AppendResult(
    AppendOutcome Outcome,
    Guid CommandId,
    IReadOnlyList<SequencedEvent> Events,
    long Head);