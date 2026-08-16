using System.Globalization;
using System.Text;

namespace NativeDCB.Ndl;

public static class NdlFormatter
{
    public static string Format(DocumentSyntax document)
    {
        ArgumentNullException.ThrowIfNull(document);
        Writer writer = new();
        for (int i = 0; i < document.Decisions.Count; i++)
        {
            if (i > 0)
            {
                writer.Line();
            }

            writer.WriteDecision(document.Decisions[i]);
        }

        return writer.ToString();
    }

    public static string Format(string text)
    {
        ParseResult result = NdlParser.Parse(text);
        if (result.HasErrors)
        {
            throw new NdlFormatException(result.Diagnostics);
        }

        return Format(result.Document);
    }

    private sealed class Writer
    {
        private readonly StringBuilder _builder = new();

        public void WriteDecision(DecisionSyntax decision)
        {
            Line($"decision {decision.Name}");
            Line($"from {decision.From.CommandType} {decision.From.Alias}");
            foreach (IncludeStageSyntax include in decision.Includes)
            {
                Line($"| include {include.EventType} {include.Alias}");
                Line($"    where {Expression(include.Where)}");
                WriteObject("    apply ", include.Apply, indent: 1, semicolon: false);
            }

            Line("| evaluate {");
            foreach (EvaluateStatementSyntax statement in decision.Evaluate.Statements)
            {
                switch (statement)
                {
                    case RequireStatementSyntax require:
                        Line($"    require {Expression(require.Condition)}");
                        Line($"        else {Expression(require.Reason)};");
                        break;
                    case LetStatementSyntax let:
                        Line($"    let {let.Name} = {Expression(let.Value)};");
                        break;
                }
            }

            Line("}");
            Line("| decide {");
            foreach (EmitStatementSyntax emission in decision.Decide.Emissions)
            {
                WriteObject($"    emit {emission.EventType} ", emission.Value, indent: 1, semicolon: true);
            }

            Line("};");
        }

        private void WriteObject(string prefix, ObjectExpressionSyntax value, int indent, bool semicolon)
        {
            Line(prefix + "{");
            for (int i = 0; i < value.Assignments.Count; i++)
            {
                AssignmentSyntax assignment = value.Assignments[i];
                string comma = i + 1 < value.Assignments.Count ? "," : string.Empty;
                Line(new string(c: ' ', (indent + 1) * 4) + assignment.Name + " = " + Expression(assignment.Value) +
                     comma);
            }

            Line(new string(c: ' ', indent * 4) + "}" + (semicolon ? ";" : string.Empty));
        }

        private static string Expression(ExpressionSyntax expression, int parentPrecedence = 0,
            bool parenthesizeEqualPrecedence = false)
        {
            int precedence = Precedence(expression);
            string text = expression switch
            {
                LiteralExpressionSyntax literal => FormatLiteral(literal),
                IdentifierExpressionSyntax identifier => identifier.Name,
                MemberAccessExpressionSyntax member => Expression(member.Target, precedence) + "." + member.Member,
                CallExpressionSyntax call => Expression(call.Target, precedence) + "(" +
                                             string.Join(", ", call.Arguments.Select(x => Expression(x))) + ")",
                ParenthesizedExpressionSyntax parenthesized => "(" + Expression(parenthesized.Expression) + ")",
                UnaryExpressionSyntax unary => Operator(unary.OperatorKind) +
                                               (unary.OperatorKind == SyntaxKind.NotKeyword ? " " : string.Empty) +
                                               Expression(unary.Operand, precedence),
                BinaryExpressionSyntax binary => Expression(
                                                      binary.Left,
                                                      precedence,
                                                      binary.OperatorKind == SyntaxKind.QuestionQuestionToken) + " " +
                                                  Operator(binary.OperatorKind) + " " + Expression(
                                                      binary.Right,
                                                      precedence,
                                                      binary.OperatorKind != SyntaxKind.QuestionQuestionToken),
                ConditionalExpressionSyntax conditional => Expression(
                                                                conditional.Condition,
                                                                precedence,
                                                                parenthesizeEqualPrecedence: true) + " ? " +
                                                            Expression(conditional.WhenTrue) + " : " +
                                                            Expression(conditional.WhenFalse, precedence),
                ObjectExpressionSyntax obj => "{ " +
                                              string.Join(", ",
                                                  obj.Assignments.Select(x => x.Name + " = " + Expression(x.Value))) +
                                              " }",
                _ => throw new ArgumentOutOfRangeException(nameof(expression))
            };

            bool needsParentheses = precedence < parentPrecedence ||
                                    (parenthesizeEqualPrecedence && precedence == parentPrecedence);
            return needsParentheses ? "(" + text + ")" : text;
        }

        private static string FormatLiteral(LiteralExpressionSyntax literal)
        {
            return literal.Kind switch
            {
                LiteralKind.Null => "null",
                LiteralKind.Boolean => (bool)literal.Value! ? "true" : "false",
                LiteralKind.Integer => ((long)literal.Value!).ToString(CultureInfo.InvariantCulture),
                LiteralKind.Decimal => FormatDecimal((decimal)literal.Value!),
                LiteralKind.String => Quote((string)literal.Value!),
                LiteralKind.Guid => ((Guid)literal.Value!).ToString("D", CultureInfo.InvariantCulture),
                LiteralKind.DateTime => ((DateTimeOffset)literal.Value!).ToString("O", CultureInfo.InvariantCulture),
                LiteralKind.Duration => FormatDuration((TimeSpan)literal.Value!),
                _ => literal.Text
            };
        }

        private static string FormatDuration(TimeSpan value)
        {
            if (value.Ticks >= TimeSpan.TicksPerDay && value.Ticks % TimeSpan.TicksPerDay == 0)
            {
                return value.TotalDays.ToString(CultureInfo.InvariantCulture) + "d";
            }

            if (value.Ticks >= TimeSpan.TicksPerHour && value.Ticks % TimeSpan.TicksPerHour == 0)
            {
                return value.TotalHours.ToString(CultureInfo.InvariantCulture) + "h";
            }

            if (value.Ticks >= TimeSpan.TicksPerMinute && value.Ticks % TimeSpan.TicksPerMinute == 0)
            {
                return value.TotalMinutes.ToString(CultureInfo.InvariantCulture) + "m";
            }

            if (value.Ticks >= TimeSpan.TicksPerSecond && value.Ticks % TimeSpan.TicksPerSecond == 0)
            {
                return value.TotalSeconds.ToString(CultureInfo.InvariantCulture) + "s";
            }

            return value.TotalMilliseconds.ToString(CultureInfo.InvariantCulture) + "ms";
        }

        private static string FormatDecimal(decimal value)
        {
            string text = value.ToString(CultureInfo.InvariantCulture);
            return text.Contains('.', StringComparison.Ordinal) ? text : text + ".0";
        }

        private static string Quote(string value)
        {
            return "\"" + value
                .Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\"", "\\\"", StringComparison.Ordinal)
                .Replace("\r", "\\r", StringComparison.Ordinal)
                .Replace("\n", "\\n", StringComparison.Ordinal)
                .Replace("\t", "\\t", StringComparison.Ordinal) + "\"";
        }

        private static int Precedence(ExpressionSyntax expression)
        {
            return expression switch
            {
                ConditionalExpressionSyntax => 1,
                BinaryExpressionSyntax binary => binary.OperatorKind switch
                {
                    SyntaxKind.QuestionQuestionToken => 2,
                    SyntaxKind.OrKeyword => 3,
                    SyntaxKind.AndKeyword => 4,
                    SyntaxKind.EqualsEqualsToken or SyntaxKind.BangEqualsToken => 5,
                    SyntaxKind.LessToken or SyntaxKind.LessOrEqualsToken or SyntaxKind.GreaterToken
                        or SyntaxKind.GreaterOrEqualsToken => 6,
                    SyntaxKind.PlusToken or SyntaxKind.MinusToken => 7,
                    _ => 8
                },
                UnaryExpressionSyntax => 9,
                MemberAccessExpressionSyntax or CallExpressionSyntax => 10,
                _ => 11
            };
        }

        private static string Operator(SyntaxKind kind)
        {
            return kind switch
            {
                SyntaxKind.PlusToken => "+",
                SyntaxKind.MinusToken => "-",
                SyntaxKind.StarToken => "*",
                SyntaxKind.SlashToken => "/",
                SyntaxKind.PercentToken => "%",
                SyntaxKind.EqualsEqualsToken => "==",
                SyntaxKind.BangEqualsToken => "!=",
                SyntaxKind.LessToken => "<",
                SyntaxKind.LessOrEqualsToken => "<=",
                SyntaxKind.GreaterToken => ">",
                SyntaxKind.GreaterOrEqualsToken => ">=",
                SyntaxKind.QuestionQuestionToken => "??",
                SyntaxKind.AndKeyword => "and",
                SyntaxKind.OrKeyword => "or",
                SyntaxKind.NotKeyword => "not",
                _ => throw new ArgumentOutOfRangeException(nameof(kind))
            };
        }

        public void Line(string text = "")
        {
            _builder.Append(text).Append(value: '\n');
        }

        public override string ToString()
        {
            return _builder.ToString();
        }
    }
}

public sealed class NdlFormatException(IReadOnlyList<NdlDiagnostic> diagnostics)
    : Exception("Cannot format invalid NDL source.")
{
    public IReadOnlyList<NdlDiagnostic> Diagnostics { get; } = diagnostics;
}