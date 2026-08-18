namespace NativeDCB.Sdk.Decisions.Diagnostics;

public sealed record SdkDiagnostic(string Code, SdkDiagnosticSeverity Severity, string Message);