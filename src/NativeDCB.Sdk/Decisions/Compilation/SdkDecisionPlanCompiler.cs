using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

using NativeDCB.Model.Decisions;
using NativeDCB.Model.Decisions.Evaluation;
using NativeDCB.Model.Decisions.Expressions;
using NativeDCB.Sdk.Decisions.Authoring;
using NativeDCB.Sdk.Decisions.Diagnostics;
using NativeDCB.Sdk.Schemas;

namespace NativeDCB.Sdk.Decisions.Compilation;

internal static class SdkDecisionPlanCompiler
{
    public static DecisionPlan Compile(string name, DecisionDefinitionState state)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (state.Diagnostics.Any(value => value.Severity == SdkDiagnosticSeverity.Error))
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine,
                state.Diagnostics.Select(value => value.Message)));
        }

        SchemaDescriptor commandSchema = SchemaDescriptor.ForCommand(state.Command.GetType());
        Dictionary<string, string> schemaFingerprints = new(StringComparer.Ordinal)
        {
            [commandSchema.Name] = Fingerprint(commandSchema.ToJsonSchemaDocument())
        };
        List<PlanInclude> includes = new(state.Includes.Count);
        for (int index = 0; index < state.Includes.Count; index++)
        {
            IncludedEventDefinition include = state.Includes[index];
            SchemaDescriptor eventSchema = SchemaDescriptor.ForEvent(include.EventType);
            schemaFingerprints[eventSchema.Name] = Fingerprint(eventSchema.ToJsonSchemaDocument());
            string alias = $"event{index}";
            PlanExpression where = Translate(
                include.Predicate.Body,
                new Dictionary<ParameterExpression, string> { [include.Predicate.Parameters[index: 0]] = alias },
                state.Command);
            IReadOnlyList<PlanKeyBinding> bindings = CompileBindings(
                include.Predicate.Body,
                include.Predicate.Parameters[index: 0],
                eventSchema,
                state.Command);
            PlanExpression reducer = Translate(
                include.Reducer.Body,
                new Dictionary<ParameterExpression, string>
                {
                    [include.Reducer.Parameters[index: 0]] = "previous",
                    [include.Reducer.Parameters[index: 1]] = alias
                },
                state.Command);
            if (reducer is not PlanObjectExpression objectReducer)
            {
                throw new InvalidOperationException("A reducer must return a structural object expression.");
            }

            includes.Add(new PlanInclude(
                eventSchema.Name,
                alias,
                where,
                bindings,
                objectReducer.Assignments));
        }

        if (state.Evaluation is null || state.Decision is null)
        {
            throw new InvalidOperationException("A decision requires Evaluate and Decide expressions.");
        }

        PlanExpression evaluation = Translate(
            state.Evaluation.Body,
            new Dictionary<ParameterExpression, string>
            {
                [state.Evaluation.Parameters[index: 0]] = "model",
                [state.Evaluation.Parameters[index: 1]] = "command"
            },
            state.Command);
        List<PlanEvaluationStep> steps = [new PlanLocal("evaluation", evaluation)];
        List<PlanEmission> emissions = new();
        CompileDecision(state.Decision, state.Command, steps, emissions, schemaFingerprints);
        if (emissions.Count == 0)
        {
            throw new InvalidOperationException("An accepted SDK decision must emit at least one event.");
        }

        string source = string.Join(separator: '|', name, commandSchema.Name,
            string.Join(separator: ';', state.Includes.Select(value => value.Reducer + ":" + value.Predicate)),
            state.Evaluation,
            state.Decision);
        return new DecisionPlan(
            name,
            commandSchema.Name,
            "command",
            includes,
            steps,
            emissions,
            new DecisionPlanFingerprints("sdk-v1", Fingerprint(source), schemaFingerprints));
    }

    private static void CompileDecision(
        LambdaExpression decision,
        object command,
        ICollection<PlanEvaluationStep> steps,
        ICollection<PlanEmission> emissions,
        IDictionary<string, string> schemaFingerprints)
    {
        Expression body = StripConvert(decision.Body);
        Dictionary<ParameterExpression, string> parameters = new()
        {
            [decision.Parameters[index: 0]] = "evaluation", [decision.Parameters[index: 1]] = "command"
        };
        if (body is ConditionalExpression conditional &&
            TryDecisionCall(conditional.IfTrue, nameof(Decision.Accept), out MethodCallExpression? accepted) &&
            TryDecisionCall(conditional.IfFalse, nameof(Decision.Reject), out MethodCallExpression? rejected) &&
            accepted is not null && rejected is not null)
        {
            steps.Add(new PlanRequirement(
                Translate(conditional.Test, parameters, command),
                Translate(rejected.Arguments[index: 0], parameters, command)));
            CompileEmissions(accepted, parameters, command, emissions, schemaFingerprints);
            return;
        }

        if (TryDecisionCall(body, nameof(Decision.Accept), out MethodCallExpression? direct) && direct is not null)
        {
            CompileEmissions(direct, parameters, command, emissions, schemaFingerprints);
            return;
        }

        throw new InvalidOperationException(
            "Decide must return Decision.Accept(...) or a conditional Accept/Reject expression.");
    }

    private static void CompileEmissions(
        MethodCallExpression accepted,
        IReadOnlyDictionary<ParameterExpression, string> parameters,
        object command,
        ICollection<PlanEmission> emissions,
        IDictionary<string, string> schemaFingerprints)
    {
        IEnumerable<Expression> values = accepted.Arguments.Count == 1 &&
                                         StripConvert(accepted.Arguments[index: 0]) is NewArrayExpression array
            ? array.Expressions
            : accepted.Arguments;
        foreach (Expression value in values)
        {
            Expression eventExpression = StripConvert(value);
            Type eventType = eventExpression.Type;
            SchemaDescriptor schema = SchemaDescriptor.ForEvent(eventType);
            schemaFingerprints[schema.Name] = Fingerprint(schema.ToJsonSchemaDocument());
            PlanExpression translated = Translate(eventExpression, parameters, command);
            if (translated is not PlanObjectExpression payload)
            {
                throw new InvalidOperationException("Accepted events must be constructed as object expressions.");
            }

            emissions.Add(new PlanEmission(schema.Name, payload.Assignments));
        }
    }

    private static IReadOnlyList<PlanKeyBinding> CompileBindings(
        Expression expression,
        ParameterExpression eventParameter,
        SchemaDescriptor schema,
        object command)
    {
        List<BinaryExpression> equalities = new();
        FlattenAnd(expression, equalities);
        List<PlanKeyBinding> result = new(equalities.Count);
        foreach (BinaryExpression equality in equalities)
        {
            MemberExpression property = DirectMember(equality.Left, eventParameter) ??
                                        DirectMember(equality.Right, eventParameter) ??
                                        throw new InvalidOperationException(
                                            "Every Where equality must bind a direct event property.");
            Expression value = property == StripConvert(equality.Left) ? equality.Right : equality.Left;
            ConsistencyKeyDescriptor key =
                schema.ConsistencyKeys.SingleOrDefault(item => item.Property == property.Member)
                ?? throw new InvalidOperationException($"Property '{property.Member.Name}' is not a consistency key.");
            _ = key;
            result.Add(new PlanKeyBinding(
                property.Member.Name,
                Translate(value, new Dictionary<ParameterExpression, string>(), command)));
        }

        return result;
    }

    private static PlanExpression Translate(
        Expression expression,
        IReadOnlyDictionary<ParameterExpression, string> parameters,
        object command)
    {
        expression = StripConvert(expression);
        if (expression is ParameterExpression parameter && parameters.TryGetValue(parameter, out string? symbol))
        {
            return new PlanSymbolExpression(symbol);
        }

        if (expression is ConstantExpression constant)
        {
            return Literal(constant.Value);
        }

        if (expression is MemberExpression member)
        {
            if (TryGetCapturedTarget(member.Expression, out object? target) &&
                ReferenceEquals(target, command))
            {
                return new PlanMemberExpression(new PlanSymbolExpression("command"), member.Member.Name);
            }

            return new PlanMemberExpression(Translate(member.Expression!, parameters, command), member.Member.Name);
        }

        if (expression is BinaryExpression binary)
        {
            return new PlanBinaryExpression(
                Translate(binary.Left, parameters, command),
                BinaryOperator(binary.NodeType),
                Translate(binary.Right, parameters, command));
        }

        if (expression is UnaryExpression unary)
        {
            return new PlanUnaryExpression(
                unary.NodeType switch
                {
                    ExpressionType.Not => PlanUnaryOperator.Not,
                    ExpressionType.Negate or ExpressionType.NegateChecked => PlanUnaryOperator.Minus,
                    ExpressionType.UnaryPlus => PlanUnaryOperator.Plus,
                    _ => throw Unsupported(expression)
                },
                Translate(unary.Operand, parameters, command));
        }

        if (expression is ConditionalExpression conditional)
        {
            return new PlanConditionalExpression(
                Translate(conditional.Test, parameters, command),
                Translate(conditional.IfTrue, parameters, command),
                Translate(conditional.IfFalse, parameters, command));
        }

        if (expression is NewExpression created)
        {
            string[] names = created.Members?.Select(createdMember => createdMember.Name).ToArray() ??
                             created.Constructor?.GetParameters().Select(constructorParameter =>
                                 FindProperty(created.Type, constructorParameter.Name!).Name).ToArray() ?? [];
            return new PlanObjectExpression(names.Zip(created.Arguments, (property, value) =>
                new PlanAssignment(property, Translate(value, parameters, command))).ToArray());
        }

        if (expression is MemberInitExpression initialized)
        {
            return new PlanObjectExpression(initialized.Bindings.Cast<MemberAssignment>().Select(binding =>
                new PlanAssignment(binding.Member.Name, Translate(binding.Expression, parameters, command))).ToArray());
        }

        if (expression is MethodCallExpression call &&
            call.Method.DeclaringType == typeof(Math) && call.Method.Name is nameof(Math.Min) or nameof(Math.Max))
        {
            return new PlanCallExpression(call.Method.Name.ToLowerInvariant(),
                call.Arguments.Select(value => Translate(value, parameters, command)).ToArray());
        }

        throw Unsupported(expression);
    }

    private static PlanLiteralExpression Literal(object? value)
    {
        return value switch
        {
            null => new PlanLiteralExpression(PlanLiteralKind.Null, Value: null),
            bool boolean => new PlanLiteralExpression(PlanLiteralKind.Boolean, boolean ? "true" : "false"),
            string text => new PlanLiteralExpression(PlanLiteralKind.String, text),
            Guid guid => new PlanLiteralExpression(PlanLiteralKind.Guid, guid.ToString("D")),
            DateTime dateTime => new PlanLiteralExpression(PlanLiteralKind.DateTime,
                dateTime.ToString("O", CultureInfo.InvariantCulture)),
            DateTimeOffset dateTime => new PlanLiteralExpression(PlanLiteralKind.DateTime,
                dateTime.ToString("O", CultureInfo.InvariantCulture)),
            TimeSpan duration => new PlanLiteralExpression(PlanLiteralKind.Duration,
                duration.ToString("c", CultureInfo.InvariantCulture)),
            byte or sbyte or short or ushort or int or uint or long or ulong =>
                new PlanLiteralExpression(PlanLiteralKind.Integer,
                    Convert.ToString(value, CultureInfo.InvariantCulture)),
            float or double or decimal =>
                new PlanLiteralExpression(PlanLiteralKind.Decimal,
                    Convert.ToString(value, CultureInfo.InvariantCulture)),
            _ => throw new InvalidOperationException(
                $"Constant type '{value.GetType().FullName}' is not supported in a decision plan.")
        };
    }

    private static bool TryDecisionCall(Expression expression, string name, out MethodCallExpression? call)
    {
        call = StripConvert(expression) as MethodCallExpression;
        return call?.Method.DeclaringType == typeof(Decision) && call.Method.Name == name;
    }

    private static bool TryGetCapturedTarget(Expression? expression, out object? value)
    {
        expression = expression is null ? null : StripConvert(expression);
        if (expression is ConstantExpression constant)
        {
            value = constant.Value;
            return true;
        }

        if (expression is MemberExpression { Member: FieldInfo field } member &&
            TryGetCapturedTarget(member.Expression, out object? target))
        {
            value = field.GetValue(target);
            return true;
        }

        value = null;
        return false;
    }

    private static void FlattenAnd(Expression expression, ICollection<BinaryExpression> equalities)
    {
        expression = StripConvert(expression);
        if (expression is BinaryExpression { NodeType: ExpressionType.AndAlso } and)
        {
            FlattenAnd(and.Left, equalities);
            FlattenAnd(and.Right, equalities);
        }
        else if (expression is BinaryExpression { NodeType: ExpressionType.Equal } equality)
        {
            equalities.Add(equality);
        }
        else
        {
            throw new InvalidOperationException("Where supports only equality and conditional-and expressions.");
        }
    }

    private static MemberExpression? DirectMember(Expression expression, ParameterExpression parameter)
    {
        return StripConvert(expression) is MemberExpression { Expression: var target } member &&
               StripConvert(target!) == parameter
            ? member
            : null;
    }

    private static PropertyInfo FindProperty(Type type, string parameterName)
    {
        return type.GetProperties().Single(property =>
            string.Equals(property.Name, parameterName, StringComparison.OrdinalIgnoreCase));
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

    private static PlanBinaryOperator BinaryOperator(ExpressionType type)
    {
        return type switch
        {
            ExpressionType.Add or ExpressionType.AddChecked => PlanBinaryOperator.Add,
            ExpressionType.Subtract or ExpressionType.SubtractChecked => PlanBinaryOperator.Subtract,
            ExpressionType.Multiply or ExpressionType.MultiplyChecked => PlanBinaryOperator.Multiply,
            ExpressionType.Divide => PlanBinaryOperator.Divide,
            ExpressionType.Modulo => PlanBinaryOperator.Remainder,
            ExpressionType.Equal => PlanBinaryOperator.Equal,
            ExpressionType.NotEqual => PlanBinaryOperator.NotEqual,
            ExpressionType.LessThan => PlanBinaryOperator.Less,
            ExpressionType.LessThanOrEqual => PlanBinaryOperator.LessOrEqual,
            ExpressionType.GreaterThan => PlanBinaryOperator.Greater,
            ExpressionType.GreaterThanOrEqual => PlanBinaryOperator.GreaterOrEqual,
            ExpressionType.AndAlso => PlanBinaryOperator.And,
            ExpressionType.OrElse => PlanBinaryOperator.Or,
            ExpressionType.Coalesce => PlanBinaryOperator.Coalesce,
            _ => throw new InvalidOperationException($"Binary expression '{type}' is not supported in a decision plan.")
        };
    }

    private static InvalidOperationException Unsupported(Expression expression)
    {
        return new InvalidOperationException(
            $"Expression node '{expression.NodeType}' is not supported in a decision plan.");
    }

    private static string Fingerprint(string value)
    {
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }
}