using NativeDCB.Model;

namespace NativeDCB.Ndl;

public sealed record CompilationResult(
    // ReSharper disable once NotAccessedPositionalProperty.Global
    SourceText Source,
    IReadOnlyList<DecisionPlan> Plans,
    IReadOnlyList<NdlDiagnostic> Diagnostics)
{
    public bool HasErrors => Diagnostics.Any(value => value.Severity == DiagnosticSeverity.Error);
}