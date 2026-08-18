using System.Linq.Expressions;

namespace NativeDCB.Sdk;

// ReSharper disable once UnusedTypeParameter
public sealed class EvaluatedDecision<TCommand, TModel, TEvaluation>
    where TCommand : notnull
{
    private readonly DecisionDefinitionState _state;

    internal EvaluatedDecision(DecisionDefinitionState state)
    {
        _state = state;
    }

    public DecisionDefinition<TCommand> Decide<TResult>(Expression<Func<TEvaluation, TCommand, TResult>> decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        _state.Decision = decision;
        return new DecisionDefinition<TCommand>(_state);
    }
}