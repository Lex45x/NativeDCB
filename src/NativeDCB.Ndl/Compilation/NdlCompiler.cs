using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using NativeDCB.Model;
using NativeDCB.Model.Decisions;
using NativeDCB.Model.Decisions.Evaluation;
using NativeDCB.Model.Decisions.Expressions;
using NativeDCB.Ndl.Formatting;
using NativeDCB.Ndl.Lexing;
using NativeDCB.Ndl.Parsing;
using NativeDCB.Ndl.Syntax;
using NativeDCB.Ndl.Syntax.Expressions;
using NativeDCB.Ndl.Syntax.Statements;

namespace NativeDCB.Ndl.Compilation;

internal static class NdlCompiler
{
    public static CompilationResult Compile(string source)
    {
        ParseResult parsed = NdlParser.Parse(source);
        if (parsed.HasErrors)
        {
            return new CompilationResult(parsed.Source, [], parsed.Diagnostics);
        }

        string sourceFingerprint = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(NdlFormatter.Format(parsed.Document)))).ToLowerInvariant();
        DecisionPlan[] plans = parsed.Document.Decisions
            .Select(decision => Compile(decision, sourceFingerprint))
            .ToArray();
        return new CompilationResult(parsed.Source, plans, parsed.Diagnostics);
    }

    private static DecisionPlan Compile(DecisionSyntax decision, string sourceFingerprint)
    {
        return new DecisionPlan(
            decision.Name,
            decision.From.CommandType,
            decision.From.Alias,
            decision.Includes.Select(Compile).ToArray(),
            decision.Evaluate.Statements.Select(Compile).ToArray(),
            decision.Decide.Emissions.Select(emission => new PlanEmission(
                emission.EventType,
                emission.Value.Assignments.Select(Compile).ToArray())).ToArray(),
            new DecisionPlanFingerprints("ndl-v1", sourceFingerprint, new Dictionary<string, string>()));
    }

    private static PlanInclude Compile(IncludeStageSyntax include)
    {
        List<(string Name, ExpressionSyntax Value)> bindings = new();
        CollectBindings(include.Where, include.Alias, bindings);
        return new PlanInclude(
            include.EventType,
            include.Alias,
            Compile(include.Where),
            bindings.Select(value => new PlanKeyBinding(value.Name, Compile(value.Value))).ToArray(),
            include.Apply.Assignments.Select(Compile).ToArray());
    }

    private static PlanEvaluationStep Compile(EvaluateStatementSyntax statement)
    {
        return statement switch
        {
            RequireStatementSyntax requirement => new PlanRequirement(
                Compile(requirement.Condition), Compile(requirement.Reason)),
            LetStatementSyntax local => new PlanLocal(local.Name, Compile(local.Value)),
            _ => throw new InvalidOperationException($"Unsupported evaluation step '{statement.GetType().Name}'.")
        };
    }

    private static PlanAssignment Compile(AssignmentSyntax assignment)
    {
        return new PlanAssignment(
            assignment.Name, Compile(assignment.Value));
    }

    private static PlanExpression Compile(ExpressionSyntax expression)
    {
        return expression switch
        {
            LiteralExpressionSyntax literal => new PlanLiteralExpression(
                (PlanLiteralKind)literal.Kind,
                LiteralValue(literal.Value)),
            IdentifierExpressionSyntax identifier => new PlanSymbolExpression(identifier.Name),
            MemberAccessExpressionSyntax member => new PlanMemberExpression(Compile(member.Target), member.Member),
            ParenthesizedExpressionSyntax parenthesized => Compile(parenthesized.Expression),
            CallExpressionSyntax { Target: IdentifierExpressionSyntax function } call => new PlanCallExpression(
                function.Name, call.Arguments.Select(Compile).ToArray()),
            CallExpressionSyntax => throw new InvalidOperationException(
                "Only named standard-library calls can be compiled."),
            UnaryExpressionSyntax unary => new PlanUnaryExpression(UnaryOperator(unary.OperatorKind),
                Compile(unary.Operand)),
            BinaryExpressionSyntax binary => new PlanBinaryExpression(
                Compile(binary.Left), BinaryOperator(binary.OperatorKind), Compile(binary.Right)),
            ConditionalExpressionSyntax conditional => new PlanConditionalExpression(
                Compile(conditional.Condition), Compile(conditional.WhenTrue), Compile(conditional.WhenFalse)),
            ObjectExpressionSyntax value => new PlanObjectExpression(value.Assignments.Select(Compile).ToArray()),
            _ => throw new InvalidOperationException($"Unsupported expression '{expression.GetType().Name}'.")
        };
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
            return;
        }

        if (TryEventMember(equality.Left, eventAlias, out string left))
        {
            bindings.Add((left, equality.Right));
        }
        else if (TryEventMember(equality.Right, eventAlias, out string right))
        {
            bindings.Add((right, equality.Left));
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

    private static string? LiteralValue(object? value)
    {
        return value switch
        {
            null => null,
            bool boolean => boolean ? "true" : "false",
            string text => text,
            IFormattable formattable => formattable.ToString(format: null, CultureInfo.InvariantCulture),
            _ => value.ToString()
        };
    }

    private static PlanUnaryOperator UnaryOperator(SyntaxKind kind)
    {
        return kind switch
        {
            SyntaxKind.NotKeyword => PlanUnaryOperator.Not,
            SyntaxKind.PlusToken => PlanUnaryOperator.Plus,
            SyntaxKind.MinusToken => PlanUnaryOperator.Minus,
            _ => throw new InvalidOperationException($"Unsupported unary operator '{kind}'.")
        };
    }

    private static PlanBinaryOperator BinaryOperator(SyntaxKind kind)
    {
        return kind switch
        {
            SyntaxKind.PlusToken => PlanBinaryOperator.Add,
            SyntaxKind.MinusToken => PlanBinaryOperator.Subtract,
            SyntaxKind.StarToken => PlanBinaryOperator.Multiply,
            SyntaxKind.SlashToken => PlanBinaryOperator.Divide,
            SyntaxKind.PercentToken => PlanBinaryOperator.Remainder,
            SyntaxKind.EqualsEqualsToken => PlanBinaryOperator.Equal,
            SyntaxKind.BangEqualsToken => PlanBinaryOperator.NotEqual,
            SyntaxKind.LessToken => PlanBinaryOperator.Less,
            SyntaxKind.LessOrEqualsToken => PlanBinaryOperator.LessOrEqual,
            SyntaxKind.GreaterToken => PlanBinaryOperator.Greater,
            SyntaxKind.GreaterOrEqualsToken => PlanBinaryOperator.GreaterOrEqual,
            SyntaxKind.AndKeyword => PlanBinaryOperator.And,
            SyntaxKind.OrKeyword => PlanBinaryOperator.Or,
            SyntaxKind.QuestionQuestionToken => PlanBinaryOperator.Coalesce,
            _ => throw new InvalidOperationException($"Unsupported binary operator '{kind}'.")
        };
    }
}