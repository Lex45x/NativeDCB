using NativeDCB.Ndl.Text;

namespace NativeDCB.Ndl.Diagnostics;

public sealed record NdlDiagnostic(string Code, string Message, DiagnosticSeverity Severity, TextSpan Span);