using NativeDCB.Model.Queries;
using NativeDCB.Sdk.Decisions.Diagnostics;

namespace NativeDCB.Sdk.Decisions.Compilation;

internal sealed record PredicateTranslation(QueryItem? QueryItem, IReadOnlyList<SdkDiagnostic> Diagnostics);