namespace NativeDCB.Sdk;

public sealed record SdkDiagnostic(string Code, SdkDiagnosticSeverity Severity, string Message);

// ReSharper disable once ClassNeverInstantiated.Global

// ReSharper disable once UnusedTypeParameter