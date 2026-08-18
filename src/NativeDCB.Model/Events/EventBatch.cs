namespace NativeDCB.Model.Events;

public sealed record EventBatch(
    Guid CommandId,
    string CommandType,
    IReadOnlyList<CandidateEvent> Events);