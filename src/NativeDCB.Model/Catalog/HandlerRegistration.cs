// Public model contracts are consumed by external clients.
// ReSharper disable UnusedType.Global
// ReSharper disable NotAccessedPositionalProperty.Global

namespace NativeDCB.Model;

public sealed record HandlerRegistration(
    string Name,
    string CommandType,
    string NdlSource,
    string SourceFingerprint,
    string PlanFingerprint);