using NativeDCB.Model;
using NativeDCB.Model.Decisions;
using NativeDCB.Ndl.Diagnostics;
using NativeDCB.Ndl.Text;

namespace NativeDCB.Ndl.Compilation;

public sealed record CompilationResult(
    // ReSharper disable once NotAccessedPositionalProperty.Global
    SourceText Source,
    IReadOnlyList<DecisionPlan> Plans,
    IReadOnlyList<NdlDiagnostic> Diagnostics)
{
    public bool HasErrors => Diagnostics.Any(value => value.Severity == DiagnosticSeverity.Error);
}