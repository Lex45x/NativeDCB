namespace NativeDCB.Model.Decisions;

public sealed record DecisionPlanFingerprints(
    string LanguageVersion,
    string SourceFingerprint,
    IReadOnlyDictionary<string, string> SchemaFingerprints);