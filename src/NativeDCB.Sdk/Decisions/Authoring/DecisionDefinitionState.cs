using System.Linq.Expressions;

namespace NativeDCB.Sdk;

internal sealed class DecisionDefinitionState(object command)
{
    public object Command { get; } = command;
    public List<IncludedEventDefinition> Includes { get; } = [];
    public List<SdkDiagnostic> Diagnostics { get; } = [];
    public LambdaExpression? Evaluation { get; set; }
    public LambdaExpression? Decision { get; set; }
}