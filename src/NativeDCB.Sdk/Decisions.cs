using System.Linq.Expressions;
using System.Reflection;

using NativeDCB.Model;

namespace NativeDCB.Sdk;

public enum SdkDiagnosticSeverity
{
    // ReSharper disable once UnusedMember.Global
    Info,

    // ReSharper disable once UnusedMember.Global
    Warning,
    Error
}

public sealed record SdkDiagnostic(string Code, SdkDiagnosticSeverity Severity, string Message);

// ReSharper disable once ClassNeverInstantiated.Global
public sealed class EmptyDecisionModel
{
    internal EmptyDecisionModel()
    {
    }
}

public sealed record IncludedEventDefinition(
    Type EventType,
    LambdaExpression Reducer,
    LambdaExpression Predicate,
    QueryItem? QueryTemplate);

internal sealed class DecisionDefinitionState(object command)
{
    public object Command { get; } = command;
    public List<IncludedEventDefinition> Includes { get; } = [];
    public List<SdkDiagnostic> Diagnostics { get; } = [];
    public LambdaExpression? Evaluation { get; set; }
    public LambdaExpression? Decision { get; set; }
}

public static class Decision
{
    public static DecisionModelBuilder<TCommand, EmptyDecisionModel> WithDecisionModel<TCommand>(TCommand command)
        where TCommand : notnull
    {
        ArgumentNullException.ThrowIfNull(command);
        return new DecisionModelBuilder<TCommand, EmptyDecisionModel>(new DecisionDefinitionState(command));
    }

    public static DecisionResultDefinition Accept(params object[] events)
    {
        return new AcceptedDecision(events);
    }

    public static DecisionResultDefinition Reject(string reason)
    {
        return new RejectedDecision(reason);
    }
}

public abstract record DecisionResultDefinition;

public sealed record AcceptedDecision(
    // ReSharper disable once NotAccessedPositionalProperty.Global
    IReadOnlyList<object> Events) : DecisionResultDefinition;

public sealed record RejectedDecision(
    // ReSharper disable once NotAccessedPositionalProperty.Global
    string Reason) : DecisionResultDefinition;

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

internal sealed record PredicateTranslation(QueryItem? QueryItem, IReadOnlyList<SdkDiagnostic> Diagnostics);

internal static class PredicateTranslator
{
    public static PredicateTranslation Translate<TEvent>(Expression<Func<TEvent, bool>> predicate)
    {
        List<SdkDiagnostic> diagnostics = new();
        SchemaDescriptor? schema = null;
        try
        {
            schema = SchemaDescriptor.ForEvent<TEvent>();
        }
        catch (InvalidOperationException exception)
        {
            diagnostics.Add(Error("NDCB100", exception.Message));
        }

        List<BinaryExpression> equalities = new();
        FlattenConjunction(predicate.Body, equalities, diagnostics);
        List<EventKey> keys = new();

        foreach (BinaryExpression equality in equalities)
        {
            Expression valueExpression;
            if (TryGetEventProperty(
                    equality.Left,
                    predicate.Parameters[index: 0],
                    out PropertyInfo property))
            {
                valueExpression = equality.Right;
            }
            else if (TryGetEventProperty(equality.Right, predicate.Parameters[index: 0], out property))
            {
                valueExpression = equality.Left;
            }
            else
            {
                diagnostics.Add(Error(
                    "NDCB103",
                    "Each equality must compare a direct event property with a command-derived value."));
                continue;
            }

            ConsistencyKeyDescriptor? key = schema?.ConsistencyKeys.SingleOrDefault(item => item.Property == property);
            if (key is null)
            {
                diagnostics.Add(Error(
                    "NDCB104",
                    $"Property '{typeof(TEvent).Name}.{property.Name}' is not marked with ConsistencyKeyAttribute."));
                continue;
            }

            if (ReferencesParameter(valueExpression, predicate.Parameters[index: 0]))
            {
                diagnostics.Add(Error("NDCB105", "A consistency-key value cannot depend on the event payload."));
                continue;
            }

            try
            {
                object? value = Expression.Lambda<Func<object?>>(
                    Expression.Convert(valueExpression, typeof(object))).Compile().Invoke();
                keys.Add(new EventKey(key.Name, SchemaDescriptor.EncodeValue(value)));
            }
            catch (Exception exception)
            {
                diagnostics.Add(Error("NDCB106",
                    $"The consistency-key value could not be evaluated: {exception.Message}"));
            }
        }

        if (equalities.Count == 0)
        {
            diagnostics.Add(Error("NDCB102", "A Where predicate must bind at least one consistency key."));
        }

        if (keys.GroupBy(key => key.Name, StringComparer.Ordinal).Any(group => group.Count() > 1))
        {
            diagnostics.Add(Error("NDCB107", "A Where predicate cannot bind the same consistency key more than once."));
        }

        QueryItem? queryItem = diagnostics.Any(item => item.Severity == SdkDiagnosticSeverity.Error) || schema is null
            ? null
            : new QueryItem([schema.Name], keys);
        return new PredicateTranslation(queryItem, diagnostics);
    }

    private static void FlattenConjunction(
        Expression expression,
        ICollection<BinaryExpression> equalities,
        ICollection<SdkDiagnostic> diagnostics)
    {
        expression = StripConvert(expression);
        if (expression is BinaryExpression { NodeType: ExpressionType.AndAlso } conjunction)
        {
            FlattenConjunction(conjunction.Left, equalities, diagnostics);
            FlattenConjunction(conjunction.Right, equalities, diagnostics);
            return;
        }

        if (expression is BinaryExpression { NodeType: ExpressionType.Equal } equality)
        {
            equalities.Add(equality);
            return;
        }

        diagnostics.Add(Error("NDCB101", "Where supports only equality and conditional-and expressions."));
    }

    private static bool TryGetEventProperty(
        Expression candidate,
        ParameterExpression eventParameter,
        out PropertyInfo property)
    {
        candidate = StripConvert(candidate);
        if (candidate is MemberExpression { Member: PropertyInfo candidateProperty, Expression: var target } &&
            StripConvert(target!) == eventParameter)
        {
            property = candidateProperty;
            return true;
        }

        property = null!;
        return false;
    }

    private static bool ReferencesParameter(Expression expression, ParameterExpression parameter)
    {
        ParameterFindingVisitor visitor = new(parameter);
        visitor.Visit(expression);
        return visitor.Found;
    }

    private static Expression StripConvert(Expression expression)
    {
        while (expression is UnaryExpression
               {
                   NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked
               } convert)
        {
            expression = convert.Operand;
        }

        return expression;
    }

    private static SdkDiagnostic Error(string code, string message)
    {
        return new SdkDiagnostic(code, SdkDiagnosticSeverity.Error, message);
    }

    private sealed class ParameterFindingVisitor(ParameterExpression parameter) : ExpressionVisitor
    {
        public bool Found { get; private set; }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            Found |= node == parameter;
            return base.VisitParameter(node);
        }
    }
}