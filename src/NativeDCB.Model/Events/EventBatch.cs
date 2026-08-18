namespace NativeDCB.Model;

public sealed record EventBatch(
    Guid CommandId,
    string CommandType,
    IReadOnlyList<CandidateEvent> Events);