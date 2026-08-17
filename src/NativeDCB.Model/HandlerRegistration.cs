namespace NativeDCB.Model;

public sealed record HandlerRegistration(
    string Name,
    string CommandType,
    string NdlSource,
    string SourceFingerprint,
    string PlanFingerprint);