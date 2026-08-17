namespace NativeDCB.Ndl;

public sealed class LexResult(
    SourceText source,
    IReadOnlyList<SyntaxToken> tokens,
    IReadOnlyList<NdlDiagnostic> diagnostics)
{
    public SourceText Source { get; } = source;
    public IReadOnlyList<SyntaxToken> Tokens { get; } = tokens;
    public IReadOnlyList<NdlDiagnostic> Diagnostics { get; } = diagnostics;

    // ReSharper disable once UnusedMember.Global
    public bool HasErrors => Diagnostics.Any(value => value.Severity == DiagnosticSeverity.Error);
}