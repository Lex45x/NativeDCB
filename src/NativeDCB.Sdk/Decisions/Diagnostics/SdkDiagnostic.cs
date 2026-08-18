namespace NativeDCB.Sdk;

public sealed record SdkDiagnostic(string Code, SdkDiagnosticSeverity Severity, string Message);