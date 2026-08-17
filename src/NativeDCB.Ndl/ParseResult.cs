namespace NativeDCB.Ndl;

public sealed class ParseResult(
    SourceText source,
    DocumentSyntax document,
    IReadOnlyList<NdlDiagnostic> diagnostics)
{
    public SourceText Source { get; } = source;
    public DocumentSyntax Document { get; } = document;
    public IReadOnlyList<NdlDiagnostic> Diagnostics { get; } = diagnostics;
    public bool HasErrors => Diagnostics.Any(value => value.Severity == DiagnosticSeverity.Error);
}