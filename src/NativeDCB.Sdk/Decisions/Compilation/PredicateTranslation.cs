using NativeDCB.Model;

namespace NativeDCB.Sdk;

internal sealed record PredicateTranslation(QueryItem? QueryItem, IReadOnlyList<SdkDiagnostic> Diagnostics);