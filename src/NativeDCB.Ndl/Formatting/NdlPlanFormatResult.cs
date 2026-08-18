namespace NativeDCB.Ndl.Formatting;

public sealed record NdlPlanFormatResult(string? NdlSource, IReadOnlyList<NdlPlanFormatDiagnostic> Diagnostics)
{
    public bool Success => NdlSource is not null;
}