using System.Linq.Expressions;

using NativeDCB.Model;

namespace NativeDCB.Sdk;

public sealed class DecisionDefinition<TCommand>
    where TCommand : notnull
{
    internal DecisionDefinition(DecisionDefinitionState state)
    {
        State = state;
    }

    private DecisionDefinitionState State { get; }

    public TCommand Command => (TCommand)State.Command;
    public IReadOnlyList<IncludedEventDefinition> Includes => State.Includes;

    public IReadOnlyList<QueryItem> QueryTemplates => State.Includes
        .Where(include => include.QueryTemplate is not null)
        .Select(include => include.QueryTemplate!)
        .ToArray();

    public LambdaExpression EvaluationExpression => State.Evaluation!;
    public LambdaExpression DecisionExpression => State.Decision!;
    public IReadOnlyList<SdkDiagnostic> Diagnostics => State.Diagnostics;
    public bool IsValid => State.Diagnostics.All(diagnostic => diagnostic.Severity != SdkDiagnosticSeverity.Error);

    public DecisionPlan Compile(string name)
    {
        return SdkDecisionPlanCompiler.Compile(name, State);
    }
}