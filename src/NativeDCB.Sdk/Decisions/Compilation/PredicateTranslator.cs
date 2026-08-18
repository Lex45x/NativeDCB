using System.Linq.Expressions;
using System.Reflection;

using NativeDCB.Model.Events;
using NativeDCB.Model.Queries;
using NativeDCB.Sdk.Decisions.Diagnostics;
using NativeDCB.Sdk.Schemas;

namespace NativeDCB.Sdk.Decisions.Compilation;

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