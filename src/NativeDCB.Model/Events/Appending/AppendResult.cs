namespace NativeDCB.Model;

public sealed record AppendResult(
    AppendOutcome Outcome,
    Guid CommandId,
    IReadOnlyList<SequencedEvent> Events,
    long Head);