using System.Linq.Expressions;

using NativeDCB.Sdk.Decisions.Diagnostics;

namespace NativeDCB.Sdk.Decisions.Authoring;

internal sealed class DecisionDefinitionState(object command)
{
    public object Command { get; } = command;
    public List<IncludedEventDefinition> Includes { get; } = [];
    public List<SdkDiagnostic> Diagnostics { get; } = [];
    public LambdaExpression? Evaluation { get; set; }
    public LambdaExpression? Decision { get; set; }
}