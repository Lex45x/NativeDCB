namespace NativeDCB.Ndl;

public sealed record NdlDiagnostic(string Code, string Message, DiagnosticSeverity Severity, TextSpan Span);