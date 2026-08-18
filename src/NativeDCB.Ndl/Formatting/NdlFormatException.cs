using NativeDCB.Ndl.Diagnostics;

namespace NativeDCB.Ndl.Formatting;

public sealed class NdlFormatException(IReadOnlyList<NdlDiagnostic> diagnostics)
    : Exception("Cannot format invalid NDL source.")
{
    public IReadOnlyList<NdlDiagnostic> Diagnostics { get; } = diagnostics;
}