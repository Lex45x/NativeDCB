namespace NativeDCB.Ndl.Formatting;

public sealed class NdlPlanFormatException(IReadOnlyList<NdlPlanFormatDiagnostic> diagnostics)
    : Exception("The decision plan cannot be represented as NDL without changing its behavior.")
{
    public IReadOnlyList<NdlPlanFormatDiagnostic> Diagnostics { get; } = diagnostics;
}