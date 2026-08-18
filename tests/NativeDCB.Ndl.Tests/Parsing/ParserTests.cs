namespace NativeDCB.Ndl.Tests;

public class ParserTests
{
    [Fact]
    public void ParsesCompleteDecisionIntoPublicAst()
    {
        ParseResult result = Ndl.Parse(TestSources.Complete);

        Assert.Empty(result.Diagnostics);
        DecisionSyntax decision = Assert.Single(result.Document.Decisions);
        Assert.Equal("SubscribeStudentToCourse", decision.Name);
        Assert.Equal("Contracts.SubscribeStudentToCourse", decision.From.CommandType);
        Assert.Equal("command", decision.From.Alias);
        Assert.Equal(expected: 2, decision.Includes.Count);
        Assert.Equal("CourseDefined", decision.Includes[index: 0].EventType);
        Assert.Equal("event", decision.Includes[index: 0].Alias);
        Assert.Equal(expected: 2, decision.Includes[index: 0].Apply.Assignments.Count);
        Assert.Collection(
            decision.Evaluate.Statements,
            x => Assert.IsType<RequireStatementSyntax>(x),
            x => Assert.IsType<RequireStatementSyntax>(x),
            x => Assert.IsType<LetStatementSyntax>(x));
        Assert.Equal(expected: 2, decision.Decide.Emissions.Count);
        Assert.Equal("AuditEntryRecorded", decision.Decide.Emissions[index: 1].EventType);
        Assert.Equal(TestSources.Complete.IndexOf("decision", StringComparison.Ordinal), decision.Span.Start);
        Assert.True(decision.Span.Length > 0);
    }

    [Fact]
    public void ParsesSeveralDecisions()
    {
        string source = TestSources.DecisionWithExpression("1") +
                        TestSources.DecisionWithExpression("2").Replace("decision Test", "decision Other");

        ParseResult result = Ndl.Parse(source);

        Assert.Empty(result.Diagnostics);
        Assert.Equal(["Test", "Other"], result.Document.Decisions.Select(x => x.Name));
    }

    [Fact]
    public void HonorsUnaryAndBinaryPrecedence()
    {
        ExpressionSyntax expression = ParseApplyExpression("not a or b and c == d + e * -f");
        BinaryExpressionSyntax or = AssertBinary(expression, SyntaxKind.OrKeyword);
        Assert.IsType<UnaryExpressionSyntax>(or.Left);
        BinaryExpressionSyntax and = AssertBinary(or.Right, SyntaxKind.AndKeyword);
        Assert.IsType<IdentifierExpressionSyntax>(and.Left);
        BinaryExpressionSyntax equals = AssertBinary(and.Right, SyntaxKind.EqualsEqualsToken);
        BinaryExpressionSyntax add = AssertBinary(equals.Right, SyntaxKind.PlusToken);
        BinaryExpressionSyntax multiply = AssertBinary(add.Right, SyntaxKind.StarToken);
        Assert.IsType<UnaryExpressionSyntax>(multiply.Right);
    }

    [Fact]
    public void ParsesNullCoalescingAsRightAssociative()
    {
        BinaryExpressionSyntax outer =
            AssertBinary(ParseApplyExpression("a ?? b ?? c"), SyntaxKind.QuestionQuestionToken);

        Assert.IsType<IdentifierExpressionSyntax>(outer.Left);
        AssertBinary(outer.Right, SyntaxKind.QuestionQuestionToken);
    }

    [Fact]
    public void ParsesConditionalMemberCallsAndObjectArguments()
    {
        ConditionalExpressionSyntax conditional = Assert.IsType<ConditionalExpressionSyntax>(
            ParseApplyExpression("exists(previous.Value) ? tools.max(1, event.Value) : 0"));

        CallExpressionSyntax exists = Assert.IsType<CallExpressionSyntax>(conditional.Condition);
        Assert.Single(exists.Arguments);
        Assert.IsType<MemberAccessExpressionSyntax>(exists.Arguments[index: 0]);
        CallExpressionSyntax max = Assert.IsType<CallExpressionSyntax>(conditional.WhenTrue);
        Assert.IsType<MemberAccessExpressionSyntax>(max.Target);
        Assert.Equal(expected: 2, max.Arguments.Count);
    }

    [Fact]
    public void PreservesParenthesizedExpression()
    {
        BinaryExpressionSyntax add = AssertBinary(ParseApplyExpression("(a + b) * c"), SyntaxKind.StarToken);
        ParenthesizedExpressionSyntax parenthesized = Assert.IsType<ParenthesizedExpressionSyntax>(add.Left);

        AssertBinary(parenthesized.Expression, SyntaxKind.PlusToken);
    }

    [Fact]
    public void ParsesEveryLiteralKind()
    {
        string[] expressions =
        [
            "null", "true", "42", "12.5", "\"text\"", "f47ac10b-58cc-4372-a567-0e02b2c3d479",
            "2026-08-13T10:15:30Z", "5m"
        ];

        LiteralKind[] kinds = expressions
            .Select(x => Assert.IsType<LiteralExpressionSyntax>(ParseApplyExpression(x)).Kind).ToArray();

        Assert.Equal(Enum.GetValues<LiteralKind>(), kinds);
    }

    private static ExpressionSyntax ParseApplyExpression(string expression)
    {
        ParseResult result = Ndl.Parse(TestSources.DecisionWithExpression(expression));
        Assert.Empty(result.Diagnostics);
        return result.Document.Decisions[index: 0].Includes[index: 0].Apply.Assignments[index: 0].Value;
    }

    private static BinaryExpressionSyntax AssertBinary(ExpressionSyntax expression, SyntaxKind kind)
    {
        BinaryExpressionSyntax binary = Assert.IsType<BinaryExpressionSyntax>(expression);
        Assert.Equal(kind, binary.OperatorKind);
        return binary;
    }
}