using System.Linq.Expressions;

namespace NativeDCB.Sdk;

public sealed class DecisionModelBuilder<TCommand, TModel>
    where TCommand : notnull
{
    private readonly DecisionDefinitionState _state;

    internal DecisionModelBuilder(DecisionDefinitionState state)
    {
        _state = state;
    }

    public DecisionModelEventSpec<TCommand, TModel, TEvent, TModel> Include<TEvent>(
        Expression<Func<TModel, TEvent, TModel>> reducer)
    {
        return Include<TEvent, TModel>(reducer);
    }

    public DecisionModelEventSpec<TCommand, TModel, TEvent, TNext> Include<TEvent, TNext>(
        Expression<Func<TModel, TEvent, TNext>> reducer)
    {
        ArgumentNullException.ThrowIfNull(reducer);
        return new DecisionModelEventSpec<TCommand, TModel, TEvent, TNext>(_state, reducer);
    }

    public EvaluatedDecision<TCommand, TModel, TEvaluation> Evaluate<TEvaluation>(
        Expression<Func<TModel, TCommand, TEvaluation>> evaluation)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        _state.Evaluation = evaluation;
        return new EvaluatedDecision<TCommand, TModel, TEvaluation>(_state);
    }
}