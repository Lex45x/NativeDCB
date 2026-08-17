using System.Globalization;

using NativeDCB.Model;

namespace NativeDCB.Ndl;

public sealed record NdlPlanFormatDiagnostic(string Path, string Message);

public sealed record NdlPlanFormatResult(string? NdlSource, IReadOnlyList<NdlPlanFormatDiagnostic> Diagnostics)
{
    public bool Success => NdlSource is not null;
}

public sealed class NdlPlanFormatException(IReadOnlyList<NdlPlanFormatDiagnostic> diagnostics)
    : Exception("The decision plan cannot be represented as NDL without changing its behavior.")
{
    public IReadOnlyList<NdlPlanFormatDiagnostic> Diagnostics { get; } = diagnostics;
}

public static class NdlPlanFormatter
{
    private static readonly TextSpan EmptySpan = new(Start: 0, Length: 0);
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "decision", "from", "include", "where", "apply", "evaluate", "require", "else", "let", "decide",
        "emit", "and", "or", "not", "true", "false", "null"
    };

    public static string Format(DecisionPlan plan)
    {
        NdlPlanFormatResult result = TryFormat(plan);
        return result.NdlSource ?? throw new NdlPlanFormatException(result.Diagnostics);
    }

    public static NdlPlanFormatResult TryFormat(DecisionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        Converter converter = new();
        DecisionSyntax? decision = converter.Decision(plan);
        if (decision is null || converter.Diagnostics.Count > 0)
        {
            return new NdlPlanFormatResult(NdlSource: null, converter.Diagnostics);
        }

        string source = NdlFormatter.Format(new DocumentSyntax([decision], EmptySpan));
        CompilationResult compiled = NdlCompiler.Compile(source);
        if (compiled.HasErrors || compiled.Plans.Count != 1)
        {
            IReadOnlyList<NdlPlanFormatDiagnostic> diagnostics = compiled.Diagnostics
                .Select(value => new NdlPlanFormatDiagnostic("GeneratedNdl", value.Message))
                .ToArray();
            return new NdlPlanFormatResult(NdlSource: null, diagnostics);
        }

        return new NdlPlanFormatResult(source, []);
    }

    private sealed class Converter
    {
        private readonly List<NdlPlanFormatDiagnostic> _diagnostics = [];

        public IReadOnlyList<NdlPlanFormatDiagnostic> Diagnostics => _diagnostics;

        public DecisionSyntax? Decision(DecisionPlan plan)
        {
            bool namesValid = Identifier(plan.Name, "Name") &&
                              TypeName(plan.CommandSchema, "CommandSchema") &&
                              Identifier(plan.CommandAlias, "CommandAlias");
            IncludeStageSyntax?[] includes = plan.Includes.Select((value, index) =>
                Include(value, $"Includes[{index}]")).ToArray();
            EvaluateStatementSyntax?[] evaluation = plan.Evaluation.Select((value, index) =>
                Evaluation(value, $"Evaluation[{index}]")).ToArray();
            EmitStatementSyntax?[] emissions = plan.Emissions.Select((value, index) =>
                Emission(value, $"Emissions[{index}]")).ToArray();
            if (!namesValid || includes.Any(value => value is null) || evaluation.Any(value => value is null) ||
                emissions.Any(value => value is null))
            {
                return null;
            }

            return new DecisionSyntax(
                plan.Name,
                new FromClauseSyntax(plan.CommandSchema, plan.CommandAlias, EmptySpan),
                includes.Cast<IncludeStageSyntax>().ToArray(),
                new EvaluateStageSyntax(evaluation.Cast<EvaluateStatementSyntax>().ToArray(), EmptySpan),
                new DecideStageSyntax(emissions.Cast<EmitStatementSyntax>().ToArray(), EmptySpan),
                EmptySpan);
        }

        private IncludeStageSyntax? Include(PlanInclude include, string path)
        {
            bool namesValid = TypeName(include.EventType, path + ".EventType") &&
                              Identifier(include.Alias, path + ".Alias");
            ExpressionSyntax? where = Expression(include.Where, path + ".Where");
            ObjectExpressionSyntax? apply = Object(include.Assignments, path + ".Assignments");
            ValidateBindings(include, path);
            return namesValid && where is not null && apply is not null
                ? new IncludeStageSyntax(include.EventType, include.Alias, where, apply, EmptySpan)
                : null;
        }

        private EvaluateStatementSyntax? Evaluation(PlanEvaluationStep? step, string path)
        {
            switch (step)
            {
                case PlanRequirement requirement:
                    {
                        ExpressionSyntax? condition = Expression(requirement.Condition, path + ".Condition");
                        ExpressionSyntax? reason = Expression(requirement.Reason, path + ".Reason");
                        return condition is not null && reason is not null
                            ? new RequireStatementSyntax(condition, reason, EmptySpan)
                            : null;
                    }
                case PlanLocal local:
                    {
                        bool nameValid = Identifier(local.Name, path + ".Name");
                        ExpressionSyntax? value = Expression(local.Value, path + ".Value");
                        return nameValid && value is not null
                            ? new LetStatementSyntax(local.Name, value, EmptySpan)
                            : null;
                    }
                default:
                    Error(path, $"Unsupported evaluation step '{step?.GetType().Name ?? "null"}'.");
                    return null;
            }
        }

        private EmitStatementSyntax? Emission(PlanEmission? emission, string path)
        {
            if (emission is null)
            {
                Error(path, "An emission cannot be null.");
                return null;
            }

            bool nameValid = TypeName(emission.EventType, path + ".EventType");
            ObjectExpressionSyntax? value = Object(emission.Assignments, path + ".Assignments");
            return nameValid && value is not null
                ? new EmitStatementSyntax(emission.EventType, value, EmptySpan)
                : null;
        }

        private ObjectExpressionSyntax? Object(IReadOnlyList<PlanAssignment> assignments, string path)
        {
            if (assignments.Count == 0)
            {
                Error(path, "NDL object expressions require at least one assignment.");
                return null;
            }

            AssignmentSyntax?[] converted = assignments.Select((value, index) =>
                Assignment(value, $"{path}[{index}]")).ToArray();
            return converted.Any(value => value is null)
                ? null
                : new ObjectExpressionSyntax(converted.Cast<AssignmentSyntax>().ToArray(), EmptySpan);
        }

        private AssignmentSyntax? Assignment(PlanAssignment? assignment, string path)
        {
            if (assignment is null)
            {
                Error(path, "An assignment cannot be null.");
                return null;
            }

            bool nameValid = Identifier(assignment.Name, path + ".Name");
            ExpressionSyntax? value = Expression(assignment.Value, path + ".Value");
            return nameValid && value is not null
                ? new AssignmentSyntax(assignment.Name, value, EmptySpan)
                : null;
        }

        private ExpressionSyntax? Expression(PlanExpression? expression, string path)
        {
            switch (expression)
            {
                case PlanLiteralExpression literal:
                    return Literal(literal, path);
                case PlanSymbolExpression symbol:
                    return Identifier(symbol.Name, path + ".Name")
                        ? new IdentifierExpressionSyntax(symbol.Name, EmptySpan)
                        : null;
                case PlanMemberExpression member:
                    {
                        ExpressionSyntax? target = Expression(member.Target, path + ".Target");
                        bool nameValid = Identifier(member.Member, path + ".Member");
                        return target is not null && nameValid
                            ? new MemberAccessExpressionSyntax(target, member.Member, EmptySpan)
                            : null;
                    }
                case PlanCallExpression call:
                    {
                        bool nameValid = Identifier(call.Function, path + ".Function");
                        ExpressionSyntax?[] arguments = call.Arguments.Select((value, index) =>
                            Expression(value, $"{path}.Arguments[{index}]")).ToArray();
                        return nameValid && arguments.All(value => value is not null)
                            ? new CallExpressionSyntax(
                                new IdentifierExpressionSyntax(call.Function, EmptySpan),
                                arguments.Cast<ExpressionSyntax>().ToArray(),
                                EmptySpan)
                            : null;
                    }
                case PlanUnaryExpression unary:
                    {
                        ExpressionSyntax? operand = Expression(unary.Operand, path + ".Operand");
                        SyntaxKind? kind = unary.Operator switch
                        {
                            PlanUnaryOperator.Not => SyntaxKind.NotKeyword,
                            PlanUnaryOperator.Plus => SyntaxKind.PlusToken,
                            PlanUnaryOperator.Minus => SyntaxKind.MinusToken,
                            _ => null
                        };
                        if (kind is null)
                        {
                            Error(path + ".Operator", $"Unsupported unary operator '{unary.Operator}'.");
                        }

                        return operand is not null && kind is not null
                            ? new UnaryExpressionSyntax(kind.Value, operand, EmptySpan)
                            : null;
                    }
                case PlanBinaryExpression binary:
                    {
                        ExpressionSyntax? left = Expression(binary.Left, path + ".Left");
                        ExpressionSyntax? right = Expression(binary.Right, path + ".Right");
                        SyntaxKind? kind = BinaryOperator(binary.Operator);
                        if (kind is null)
                        {
                            Error(path + ".Operator", $"Unsupported binary operator '{binary.Operator}'.");
                        }

                        return left is not null && right is not null && kind is not null
                            ? new BinaryExpressionSyntax(left, kind.Value, right, EmptySpan)
                            : null;
                    }
                case PlanConditionalExpression conditional:
                    {
                        ExpressionSyntax? condition = Expression(conditional.Condition, path + ".Condition");
                        ExpressionSyntax? whenTrue = Expression(conditional.WhenTrue, path + ".WhenTrue");
                        ExpressionSyntax? whenFalse = Expression(conditional.WhenFalse, path + ".WhenFalse");
                        return condition is not null && whenTrue is not null && whenFalse is not null
                            ? new ConditionalExpressionSyntax(condition, whenTrue, whenFalse, EmptySpan)
                            : null;
                    }
                case PlanObjectExpression value:
                    return Object(value.Assignments, path + ".Assignments");
                default:
                    Error(path, $"Unsupported expression '{expression?.GetType().Name ?? "null"}'.");
                    return null;
            }
        }

        private ExpressionSyntax? Literal(PlanLiteralExpression literal, string path)
        {
            try
            {
                object? value = literal.Kind switch
                {
                    PlanLiteralKind.Null => null,
                    PlanLiteralKind.Boolean => bool.Parse(literal.Value!),
                    PlanLiteralKind.Integer => long.Parse(literal.Value!, CultureInfo.InvariantCulture),
                    PlanLiteralKind.Decimal => decimal.Parse(literal.Value!, CultureInfo.InvariantCulture),
                    PlanLiteralKind.String when literal.Value is not null => literal.Value,
                    PlanLiteralKind.String => throw new FormatException("A string literal cannot be null."),
                    PlanLiteralKind.Guid => Guid.Parse(literal.Value!),
                    PlanLiteralKind.DateTime => DateTimeOffset.Parse(
                        literal.Value!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    PlanLiteralKind.Duration => ParseDuration(literal.Value),
                    _ => throw new FormatException($"Unsupported literal kind '{literal.Kind}'.")
                };
                return new LiteralExpressionSyntax((LiteralKind)literal.Kind, value, literal.Value ?? "null", EmptySpan);
            }
            catch (Exception exception) when (exception is FormatException or OverflowException or ArgumentNullException)
            {
                Error(path, $"Invalid {literal.Kind} literal: {exception.Message}");
                return null;
            }
        }

        private static TimeSpan ParseDuration(string? value)
        {
            TimeSpan duration = TimeSpan.Parse(value!, CultureInfo.InvariantCulture);
            return duration < TimeSpan.Zero
                ? throw new FormatException("Negative durations have no equivalent NDL literal.")
                : duration;
        }

        private void ValidateBindings(PlanInclude include, string path)
        {
            List<PlanKeyBinding> derived = [];
            CollectBindings(include.Where, include.Alias, derived);
            if (derived.Count != include.KeyBindings.Count)
            {
                Error(path + ".KeyBindings",
                    "Stored key bindings do not match the bindings derivable from the NDL where expression.");
                return;
            }

            for (int index = 0; index < derived.Count; index++)
            {
                PlanKeyBinding expected = include.KeyBindings[index];
                PlanKeyBinding actual = derived[index];
                if (!string.Equals(expected.PropertyName, actual.PropertyName, StringComparison.Ordinal) ||
                    !Equivalent(expected.Value, actual.Value))
                {
                    Error($"{path}.KeyBindings[{index}]",
                        "Stored key binding does not match the binding derivable from the NDL where expression.");
                }
            }
        }

        private static void CollectBindings(PlanExpression expression, string alias, ICollection<PlanKeyBinding> result)
        {
            if (expression is PlanBinaryExpression { Operator: PlanBinaryOperator.And } conjunction)
            {
                CollectBindings(conjunction.Left, alias, result);
                CollectBindings(conjunction.Right, alias, result);
                return;
            }

            if (expression is not PlanBinaryExpression { Operator: PlanBinaryOperator.Equal } equality)
            {
                return;
            }

            if (TryEventMember(equality.Left, alias, out string left))
            {
                result.Add(new PlanKeyBinding(left, equality.Right));
            }
            else if (TryEventMember(equality.Right, alias, out string right))
            {
                result.Add(new PlanKeyBinding(right, equality.Left));
            }
        }

        private static bool TryEventMember(PlanExpression expression, string alias, out string member)
        {
            if (expression is PlanMemberExpression
                {
                    Target: PlanSymbolExpression symbol
                } access && (symbol.Name == alias || symbol.Name == "event"))
            {
                member = access.Member;
                return true;
            }

            member = string.Empty;
            return false;
        }

        private static bool Equivalent(PlanExpression? left, PlanExpression? right)
        {
            return (left, right) switch
            {
                (PlanLiteralExpression x, PlanLiteralExpression y) => x.Kind == y.Kind && x.Value == y.Value,
                (PlanSymbolExpression x, PlanSymbolExpression y) => x.Name == y.Name,
                (PlanMemberExpression x, PlanMemberExpression y) =>
                    x.Member == y.Member && Equivalent(x.Target, y.Target),
                (PlanCallExpression x, PlanCallExpression y) =>
                    x.Function == y.Function && Equivalent(x.Arguments, y.Arguments),
                (PlanUnaryExpression x, PlanUnaryExpression y) =>
                    x.Operator == y.Operator && Equivalent(x.Operand, y.Operand),
                (PlanBinaryExpression x, PlanBinaryExpression y) =>
                    x.Operator == y.Operator && Equivalent(x.Left, y.Left) && Equivalent(x.Right, y.Right),
                (PlanConditionalExpression x, PlanConditionalExpression y) =>
                    Equivalent(x.Condition, y.Condition) && Equivalent(x.WhenTrue, y.WhenTrue) &&
                    Equivalent(x.WhenFalse, y.WhenFalse),
                (PlanObjectExpression x, PlanObjectExpression y) => Equivalent(x.Assignments, y.Assignments),
                _ => false
            };
        }

        private static bool Equivalent(IReadOnlyList<PlanExpression> left, IReadOnlyList<PlanExpression> right)
        {
            return left.Count == right.Count && left.Zip(right).All(pair => Equivalent(pair.First, pair.Second));
        }

        private static bool Equivalent(IReadOnlyList<PlanAssignment> left, IReadOnlyList<PlanAssignment> right)
        {
            return left.Count == right.Count && left.Zip(right).All(pair =>
                pair.First.Name == pair.Second.Name && Equivalent(pair.First.Value, pair.Second.Value));
        }

        private bool TypeName(string value, string path)
        {
            string[] parts = value.Split('.');
            if (parts.Length > 0 && parts.All(IsIdentifier))
            {
                return true;
            }

            Error(path, $"'{value}' is not a valid NDL type name.");
            return false;
        }

        private bool Identifier(string value, string path)
        {
            if (IsIdentifier(value))
            {
                return true;
            }

            Error(path, $"'{value}' is not a valid NDL identifier.");
            return false;
        }

        private static bool IsIdentifier(string value)
        {
            return !string.IsNullOrEmpty(value) &&
                   !Keywords.Contains(value) &&
                   (char.IsLetter(value[index: 0]) || value[index: 0] == '_') &&
                   value.Skip(count: 1).All(character => char.IsLetterOrDigit(character) || character == '_');
        }

        private void Error(string path, string message)
        {
            _diagnostics.Add(new NdlPlanFormatDiagnostic(path, message));
        }

        private static SyntaxKind? BinaryOperator(PlanBinaryOperator value)
        {
            return value switch
            {
                PlanBinaryOperator.Add => SyntaxKind.PlusToken,
                PlanBinaryOperator.Subtract => SyntaxKind.MinusToken,
                PlanBinaryOperator.Multiply => SyntaxKind.StarToken,
                PlanBinaryOperator.Divide => SyntaxKind.SlashToken,
                PlanBinaryOperator.Remainder => SyntaxKind.PercentToken,
                PlanBinaryOperator.Equal => SyntaxKind.EqualsEqualsToken,
                PlanBinaryOperator.NotEqual => SyntaxKind.BangEqualsToken,
                PlanBinaryOperator.Less => SyntaxKind.LessToken,
                PlanBinaryOperator.LessOrEqual => SyntaxKind.LessOrEqualsToken,
                PlanBinaryOperator.Greater => SyntaxKind.GreaterToken,
                PlanBinaryOperator.GreaterOrEqual => SyntaxKind.GreaterOrEqualsToken,
                PlanBinaryOperator.And => SyntaxKind.AndKeyword,
                PlanBinaryOperator.Or => SyntaxKind.OrKeyword,
                PlanBinaryOperator.Coalesce => SyntaxKind.QuestionQuestionToken,
                _ => null
            };
        }
    }
}