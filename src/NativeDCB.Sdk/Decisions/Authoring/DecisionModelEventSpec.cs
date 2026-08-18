using System.Linq.Expressions;

using NativeDCB.Sdk.Decisions.Compilation;

namespace NativeDCB.Sdk.Decisions.Authoring;

public sealed class DecisionModelEventSpec<TCommand, TPrevious, TEvent, TNext>
    where TCommand : notnull
{
    private readonly Expression<Func<TPrevious, TEvent, TNext>> _reducer;
    private readonly DecisionDefinitionState _state;

    internal DecisionModelEventSpec(
        DecisionDefinitionState state,
        Expression<Func<TPrevious, TEvent, TNext>> reducer)
    {
        _state = state;
        _reducer = reducer;
    }

    public DecisionModelBuilder<TCommand, TNext> Where(Expression<Func<TEvent, bool>> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        PredicateTranslation translation = PredicateTranslator.Translate(predicate);
        _state.Diagnostics.AddRange(translation.Diagnostics);
        _state.Includes.Add(new IncludedEventDefinition(typeof(TEvent), _reducer, predicate, translation.QueryItem));
        return new DecisionModelBuilder<TCommand, TNext>(_state);
    }
}