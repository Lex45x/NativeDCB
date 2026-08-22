using System.Globalization;
using System.Text.Json;

using NativeDCB.Model.Decisions;
using NativeDCB.Model.Decisions.Evaluation;
using NativeDCB.Model.Decisions.Expressions;
using NativeDCB.Model.Events;

namespace NativeDCB.Actors.Decisions.Execution;

internal static class DecisionModelKernel
{
    private static readonly object Missing = new();

    internal static Dictionary<string, object?> Replay(
        DecisionPlan decision,
        JsonElement command,
        IReadOnlyList<SequencedEvent> events,
        bool ensureInitialized)
    {
        Dictionary<string, object?> model = new(StringComparer.Ordinal);
        foreach (SequencedEvent persisted in events)
        {
            object? eventValue = ConvertJson(persisted.Data);
            foreach (PlanInclude include in decision.Includes.Where(x => x.EventType == persisted.Type))
            {
                Dictionary<string, object?> scope = BaseScope(decision, command, model);
                scope[include.Alias] = eventValue;
                scope["event"] = eventValue;
                scope["position"] = persisted.EventId;
                if (!ToBoolean(Evaluate(include.Where, scope)))
                {
                    continue;
                }

                Dictionary<string, object?> previous = new(model, StringComparer.Ordinal);
                scope["model"] = previous;
                scope["previous"] = previous;
                object?[] values = include.Assignments
                    .Select(assignment => Evaluate(assignment.Value, scope))
                    .ToArray();
                for (int index = 0; index < include.Assignments.Count; index++)
                {
                    model[include.Assignments[index].Name] = values[index];
                }
            }
        }

        if (ensureInitialized)
        {
            EnsureInitialized(model);
        }

        return model;
    }

    internal static DecisionModelEvaluationResult Evaluate(
        DecisionPlan decision,
        JsonElement command,
        Dictionary<string, object?> model)
    {
        Dictionary<string, object?> scope = BaseScope(decision, command, model);
        foreach (PlanEvaluationStep statement in decision.Evaluation)
        {
            switch (statement)
            {
                case PlanRequirement require when !ToBoolean(Evaluate(require.Condition, scope)):
                    return new DecisionModelEvaluationResult(
                        Rejected: true,
                        ScalarText(Evaluate(require.Reason, scope)),
                        []);
                case PlanLocal let:
                    scope[let.Name] = Evaluate(let.Value, scope);
                    break;
            }
        }

        EvaluatedDecisionEmission[] emissions = decision.Emissions.Select(emission =>
        {
            Dictionary<string, object?> payload = EvaluateObject(emission.Assignments, scope);
            if (payload.Values.Any(value => ReferenceEquals(value, Missing)))
            {
                throw new InvalidOperationException(
                    $"Emitted event '{emission.EventType}' contains an uninitialized property.");
            }

            return new EvaluatedDecisionEmission(emission.EventType, payload);
        }).ToArray();
        return new DecisionModelEvaluationResult(Rejected: false, RejectionReason: null, emissions);
    }

    internal static string EvaluateScalar(
        DecisionPlan decision,
        JsonElement command,
        PlanExpression expression)
    {
        return ScalarText(Evaluate(
            expression,
            BaseScope(decision, command, new Dictionary<string, object?>())));
    }

    private static void EnsureInitialized(object? value)
    {
        if (ReferenceEquals(value, Missing))
        {
            throw new InvalidOperationException("The prepared decision model contains an uninitialized value.");
        }

        IEnumerable<object?> children = value switch
        {
            Dictionary<string, object?> dictionary => dictionary.Values,
            object?[] array => array,
            _ => []
        };
        foreach (object? child in children)
        {
            EnsureInitialized(child);
        }
    }

    private static Dictionary<string, object?> BaseScope(
        DecisionPlan decision,
        JsonElement command,
        Dictionary<string, object?> model)
    {
        object? commandValue = ConvertJson(command);
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [decision.CommandAlias] = commandValue,
            ["command"] = commandValue,
            ["model"] = model,
            ["previous"] = model
        };
    }

    private static object? Evaluate(PlanExpression expression, Dictionary<string, object?> scope)
    {
        return expression switch
        {
            PlanLiteralExpression literal => EvaluateLiteral(literal),
            PlanSymbolExpression symbol => scope.TryGetValue(symbol.Name, out object? value) ? value : Missing,
            PlanMemberExpression member => ReadMember(Evaluate(member.Target, scope), member.Member),
            PlanObjectExpression objectValue => EvaluateObject(objectValue.Assignments, scope),
            PlanUnaryExpression unary => EvaluateUnary(unary, scope),
            PlanBinaryExpression binary => EvaluateBinary(binary, scope),
            PlanConditionalExpression conditional => ToBoolean(Evaluate(conditional.Condition, scope))
                ? Evaluate(conditional.WhenTrue, scope)
                : Evaluate(conditional.WhenFalse, scope),
            PlanCallExpression call => EvaluateCall(call, scope),
            _ => throw new InvalidOperationException($"Unsupported plan expression '{expression.GetType().Name}'.")
        };
    }

    private static Dictionary<string, object?> EvaluateObject(
        IReadOnlyList<PlanAssignment> assignments,
        Dictionary<string, object?> scope)
    {
        return assignments.ToDictionary(
            assignment => assignment.Name,
            assignment => Evaluate(assignment.Value, scope),
            StringComparer.Ordinal);
    }

    private static object? EvaluateLiteral(PlanLiteralExpression literal)
    {
        return literal.Kind switch
        {
            PlanLiteralKind.Null => null,
            PlanLiteralKind.Boolean => bool.Parse(literal.Value!),
            PlanLiteralKind.Integer => long.Parse(literal.Value!, CultureInfo.InvariantCulture),
            PlanLiteralKind.Decimal => decimal.Parse(literal.Value!, CultureInfo.InvariantCulture),
            PlanLiteralKind.String => literal.Value,
            PlanLiteralKind.Guid => Guid.Parse(literal.Value!),
            PlanLiteralKind.DateTime => DateTimeOffset.Parse(literal.Value!, CultureInfo.InvariantCulture),
            PlanLiteralKind.Duration => TimeSpan.Parse(literal.Value!, CultureInfo.InvariantCulture),
            _ => throw new InvalidOperationException($"Unsupported literal kind '{literal.Kind}'.")
        };
    }

    private static object EvaluateUnary(PlanUnaryExpression unary, Dictionary<string, object?> scope)
    {
        object? value = Evaluate(unary.Operand, scope);
        return unary.Operator switch
        {
            PlanUnaryOperator.Not => !ToBoolean(value),
            PlanUnaryOperator.Plus => Number(value),
            PlanUnaryOperator.Minus => -Number(value),
            _ => throw new InvalidOperationException("Unsupported unary operator.")
        };
    }

    private static object? EvaluateBinary(PlanBinaryExpression binary, Dictionary<string, object?> scope)
    {
        object? left = Evaluate(binary.Left, scope);
        if (binary.Operator == PlanBinaryOperator.Coalesce)
        {
            return left is null || ReferenceEquals(left, Missing) ? Evaluate(binary.Right, scope) : left;
        }

        if (binary.Operator == PlanBinaryOperator.And && !ToBoolean(left))
        {
            return false;
        }

        if (binary.Operator == PlanBinaryOperator.Or && ToBoolean(left))
        {
            return true;
        }

        object? right = Evaluate(binary.Right, scope);
        return binary.Operator switch
        {
            PlanBinaryOperator.Add when left is string || right is string => ScalarText(left) + ScalarText(right),
            PlanBinaryOperator.Add => Number(left) + Number(right),
            PlanBinaryOperator.Subtract => Number(left) - Number(right),
            PlanBinaryOperator.Multiply => Number(left) * Number(right),
            PlanBinaryOperator.Divide => Number(left) / Number(right),
            PlanBinaryOperator.Remainder => Number(left) % Number(right),
            PlanBinaryOperator.Equal => Equal(left, right),
            PlanBinaryOperator.NotEqual => !Equal(left, right),
            PlanBinaryOperator.Less => Compare(left, right) < 0,
            PlanBinaryOperator.LessOrEqual => Compare(left, right) <= 0,
            PlanBinaryOperator.Greater => Compare(left, right) > 0,
            PlanBinaryOperator.GreaterOrEqual => Compare(left, right) >= 0,
            PlanBinaryOperator.And => ToBoolean(right),
            PlanBinaryOperator.Or => ToBoolean(right),
            _ => throw new InvalidOperationException("Unsupported binary operator.")
        };
    }

    private static object? EvaluateCall(PlanCallExpression call, Dictionary<string, object?> scope)
    {
        object?[] arguments = call.Arguments.Select(value => Evaluate(value, scope)).ToArray();
        return call.Function switch
        {
            "exists" when arguments.Length == 1 => !ReferenceEquals(arguments[0], Missing),
            "min" when arguments.Length == 2 => Number(arguments[0]) <= Number(arguments[1])
                ? arguments[0]
                : arguments[1],
            "max" when arguments.Length == 2 => Number(arguments[0]) >= Number(arguments[1])
                ? arguments[0]
                : arguments[1],
            _ => throw new InvalidOperationException($"Unknown function '{call.Function}'.")
        };
    }

    private static object? ReadMember(object? target, string member)
    {
        if (target is Dictionary<string, object?> dictionary && dictionary.TryGetValue(member, out object? value))
        {
            return value;
        }

        return Missing;
    }

    private static object? ConvertJson(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Object => value.EnumerateObject().ToDictionary(
                property => property.Name, property => ConvertJson(property.Value), StringComparer.Ordinal),
            JsonValueKind.Array => value.EnumerateArray().Select(ConvertJson).ToArray(),
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number when value.TryGetInt64(out long integer) => integer,
            JsonValueKind.Number => value.GetDecimal(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => Missing
        };
    }

    private static bool ToBoolean(object? value)
    {
        if (ReferenceEquals(value, Missing))
        {
            throw new InvalidOperationException(
                "An uninitialized value was read without an exists check or null-coalescing default.");
        }

        return value is bool boolean
            ? boolean
            : throw new InvalidOperationException("Expression must evaluate to a boolean.");
    }

    private static decimal Number(object? value)
    {
        if (ReferenceEquals(value, Missing))
        {
            throw new InvalidOperationException(
                "An uninitialized value was read without an exists check or null-coalescing default.");
        }

        return value switch
        {
            byte number => number,
            short number => number,
            int number => number,
            long number => number,
            float number => (decimal)number,
            double number => (decimal)number,
            decimal number => number,
            _ => throw new InvalidOperationException("Expression must evaluate to a number.")
        };
    }

    private static bool Equal(object? left, object? right)
    {
        if (ReferenceEquals(left, Missing) || ReferenceEquals(right, Missing))
        {
            throw new InvalidOperationException(
                "An uninitialized value was read without an exists check or null-coalescing default.");
        }

        if (IsNumber(left) && IsNumber(right))
        {
            return Number(left) == Number(right);
        }

        return Equals(left, right);
    }

    private static int Compare(object? left, object? right)
    {
        if (ReferenceEquals(left, Missing) || ReferenceEquals(right, Missing))
        {
            throw new InvalidOperationException(
                "An uninitialized value was read without an exists check or null-coalescing default.");
        }

        if (IsNumber(left) && IsNumber(right))
        {
            return Number(left).CompareTo(Number(right));
        }

        return string.Compare(ScalarText(left), ScalarText(right), StringComparison.Ordinal);
    }

    private static bool IsNumber(object? value)
    {
        return value is byte or short or int or long or float or double or decimal;
    }

    private static string ScalarText(object? value)
    {
        if (ReferenceEquals(value, Missing))
        {
            throw new InvalidOperationException("An uninitialized value was read.");
        }

        return value switch
        {
            null => "null",
            bool boolean => boolean ? "true" : "false",
            IFormattable formattable => formattable.ToString(format: null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty
        };
    }
}

internal sealed record DecisionModelEvaluationResult(
    bool Rejected,
    string? RejectionReason,
    IReadOnlyList<EvaluatedDecisionEmission> Emissions);

internal sealed record EvaluatedDecisionEmission(
    string EventType,
    Dictionary<string, object?> Payload);