using NativeDCB.Actors.Catalog.Schemas;
using NativeDCB.Ndl.Lexing;
using NativeDCB.Ndl.Parsing;
using NativeDCB.Ndl.Syntax;
using NativeDCB.Ndl.Syntax.Expressions;
using NativeDCB.Ndl.Syntax.Statements;

namespace NativeDCB.Actors.Catalog;

internal static class NdlDecisionValidator
{
    public static IReadOnlyList<string> Validate(
        ParseResult parsed,
        string? commandType = null,
        IReadOnlyDictionary<string, RegisteredJsonSchema>? eventSchemas = null)
    {
        List<string> errors = new();
        if (commandType is not null && parsed.Document.Decisions.Count != 1)
        {
            errors.Add("Handler NDL must contain exactly one decision.");
            return errors;
        }

        foreach (IGrouping<string, DecisionSyntax> duplicate in parsed.Document.Decisions
                     .GroupBy(value => value.Name, StringComparer.Ordinal)
                     .Where(value => value.Count() > 1))
        {
            errors.Add($"Decision name '{duplicate.Key}' is declared more than once.");
        }

        foreach (DecisionSyntax decision in parsed.Document.Decisions)
        {
            ValidateDecision(decision, commandType, eventSchemas, errors);
        }

        return errors;
    }

    private static void ValidateDecision(
        DecisionSyntax decision,
        string? commandType,
        IReadOnlyDictionary<string, RegisteredJsonSchema>? eventSchemas,
        List<string> errors)
    {
        if (commandType is not null && !string.Equals(decision.From.CommandType, commandType, StringComparison.Ordinal))
        {
            errors.Add(
                $"Handler command type '{commandType}' does not match NDL command type '{decision.From.CommandType}'.");
        }

        foreach (IncludeStageSyntax include in decision.Includes)
        {
            List<(string Name, ExpressionSyntax Value)> bindings = new();
            try
            {
                CollectBindings(include.Where, include.Alias, bindings);
            }
            catch (InvalidOperationException exception)
            {
                errors.Add(exception.Message);
            }

            if (bindings.Count == 0)
            {
                errors.Add($"Include '{include.EventType}' must bind at least one event key.");
            }

            if (eventSchemas is not null &&
                eventSchemas.TryGetValue(include.EventType, out RegisteredJsonSchema? includeSchema))
            {
                foreach ((string property, _) in bindings)
                {
                    try
                    {
                        _ = includeSchema.ResolveKeyName(property);
                    }
                    catch (InvalidOperationException exception)
                    {
                        errors.Add(exception.Message);
                    }
                }
            }

            ValidateObject(include.Apply, $"include '{include.EventType}' apply", errors);
        }

        foreach (EmitStatementSyntax emission in decision.Decide.Emissions)
        {
            ValidateObject(emission.Value, $"emit '{emission.EventType}'", errors);
            if (eventSchemas is not null &&
                eventSchemas.TryGetValue(emission.EventType, out RegisteredJsonSchema? emittedSchema))
            {
                HashSet<string> assigned = emission.Value.Assignments
                    .Select(assignment => assignment.Name)
                    .ToHashSet(StringComparer.Ordinal);
                foreach (JsonPropertySchema property in emittedSchema.Properties.Values)
                {
                    if ((property.Required || property.KeyName is not null) && !assigned.Contains(property.Name))
                    {
                        errors.Add($"Emitted event '{emission.EventType}' must assign property '{property.Name}'.");
                    }
                }

                if (!emittedSchema.AdditionalProperties)
                {
                    foreach (string property in assigned.Where(value => !emittedSchema.Properties.ContainsKey(value)))
                    {
                        errors.Add($"Emitted event '{emission.EventType}' assigns undeclared property '{property}'.");
                    }
                }
            }
        }
    }

    private static void ValidateObject(ObjectExpressionSyntax value, string role, List<string> errors)
    {
        string? duplicate = value.Assignments.GroupBy(item => item.Name, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicate is not null)
        {
            errors.Add($"The {role} assigns '{duplicate}' more than once.");
        }

        foreach (CallExpressionSyntax call in value.Assignments.SelectMany(item => Calls(item.Value)))
        {
            if (call.Target is not IdentifierExpressionSyntax function ||
                function.Name is not ("exists" or "min" or "max"))
            {
                errors.Add($"The {role} contains an unsupported function call.");
            }
        }
    }

    private static IEnumerable<CallExpressionSyntax> Calls(ExpressionSyntax value)
    {
        if (value is CallExpressionSyntax call)
        {
            yield return call;
        }

        IEnumerable<ExpressionSyntax> children = value switch
        {
            MemberAccessExpressionSyntax member => [member.Target],
            CallExpressionSyntax invocation => [invocation.Target, .. invocation.Arguments],
            ParenthesizedExpressionSyntax parenthesized => [parenthesized.Expression],
            UnaryExpressionSyntax unary => [unary.Operand],
            BinaryExpressionSyntax binary => [binary.Left, binary.Right],
            ConditionalExpressionSyntax conditional =>
                [conditional.Condition, conditional.WhenTrue, conditional.WhenFalse],
            ObjectExpressionSyntax objectValue => objectValue.Assignments.Select(item => item.Value),
            _ => []
        };
        foreach (ExpressionSyntax child in children)
        {
            foreach (CallExpressionSyntax nested in Calls(child))
            {
                yield return nested;
            }
        }
    }

    private static void CollectBindings(
        ExpressionSyntax expression,
        string eventAlias,
        List<(string Name, ExpressionSyntax Value)> bindings)
    {
        if (expression is BinaryExpressionSyntax { OperatorKind: SyntaxKind.AndKeyword } conjunction)
        {
            CollectBindings(conjunction.Left, eventAlias, bindings);
            CollectBindings(conjunction.Right, eventAlias, bindings);
            return;
        }

        if (expression is not BinaryExpressionSyntax { OperatorKind: SyntaxKind.EqualsEqualsToken } equality)
        {
            throw new InvalidOperationException(
                "Where expressions support only event-key equality bindings joined by 'and'.");
        }

        if (TryEventMember(equality.Left, eventAlias, out string left))
        {
            bindings.Add((left, equality.Right));
        }
        else if (TryEventMember(equality.Right, eventAlias, out string right))
        {
            bindings.Add((right, equality.Left));
        }
        else
        {
            throw new InvalidOperationException("A where equality must compare a direct event property.");
        }
    }

    private static bool TryEventMember(ExpressionSyntax expression, string alias, out string member)
    {
        if (expression is MemberAccessExpressionSyntax
            {
                Target: IdentifierExpressionSyntax identifier
            } access && (identifier.Name == alias || identifier.Name == "event"))
        {
            member = access.Member;
            return true;
        }

        member = string.Empty;
        return false;
    }
}