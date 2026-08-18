using NativeDCB.Ndl.Diagnostics;
using NativeDCB.Ndl.Lexing;
using NativeDCB.Ndl.Text;

namespace NativeDCB.Ndl.Tests.Lexing;

public class LexerTests
{
    [Fact]
    public void LexesKeywordsOperatorsAndSkipsComments()
    {
        LexResult result = Ndl.Lex("decision // line\n from /* block */ and or not ?? == != <= >= + - * / % ? :");

        Assert.Empty(result.Diagnostics);
        Assert.Equal(
            [
                SyntaxKind.DecisionKeyword, SyntaxKind.FromKeyword, SyntaxKind.AndKeyword,
                SyntaxKind.OrKeyword, SyntaxKind.NotKeyword, SyntaxKind.QuestionQuestionToken,
                SyntaxKind.EqualsEqualsToken, SyntaxKind.BangEqualsToken, SyntaxKind.LessOrEqualsToken,
                SyntaxKind.GreaterOrEqualsToken, SyntaxKind.PlusToken, SyntaxKind.MinusToken,
                SyntaxKind.StarToken, SyntaxKind.SlashToken, SyntaxKind.PercentToken,
                SyntaxKind.QuestionToken, SyntaxKind.ColonToken, SyntaxKind.EndOfFileToken
            ],
            result.Tokens.Select(x => x.Kind));
    }

    [Theory]
    [InlineData("42", SyntaxKind.IntegerToken, 42L)]
    [InlineData("12.50", SyntaxKind.DecimalToken, 12.50)]
    [InlineData("true", SyntaxKind.TrueKeyword, true)]
    [InlineData("false", SyntaxKind.FalseKeyword, false)]
    public void LexesPrimitiveLiteralValues(string text, SyntaxKind kind, object expected)
    {
        SyntaxToken token = Assert.Single(Ndl.Lex(text).Tokens, x => x.Kind != SyntaxKind.EndOfFileToken);

        Assert.Equal(kind, token.Kind);
        Assert.Equal(Convert.ToDecimal(expected), Convert.ToDecimal(token.Value));
        Assert.Equal(new TextSpan(Start: 0, text.Length), token.Span);
    }

    [Fact]
    public void LexesStringEscapes()
    {
        LexResult result = Ndl.Lex("\"line\\n\\\"quoted\\\"\\\\\"");
        SyntaxToken token = Assert.Single(result.Tokens, x => x.Kind == SyntaxKind.StringToken);

        Assert.Empty(result.Diagnostics);
        Assert.Equal("line\n\"quoted\"\\", token.Value);
    }

    [Theory]
    [InlineData("f47ac10b-58cc-4372-a567-0e02b2c3d479", SyntaxKind.GuidToken)]
    [InlineData("2026-08-13T10:15:30Z", SyntaxKind.DateTimeToken)]
    [InlineData("250ms", SyntaxKind.DurationToken)]
    [InlineData("2.5h", SyntaxKind.DurationToken)]
    public void LexesTypedLiterals(string text, SyntaxKind expectedKind)
    {
        LexResult result = Ndl.Lex(text);

        Assert.Empty(result.Diagnostics);
        Assert.Equal(expectedKind, result.Tokens[index: 0].Kind);
    }

    [Theory]
    [InlineData("@", "NDL0001")]
    [InlineData("/* missing", "NDL0002")]
    [InlineData("\"missing", "NDL0003")]
    [InlineData("1fortnight", "NDL0005")]
    public void ReportsLexicalErrorsWithSpans(string text, string code)
    {
        NdlDiagnostic diagnostic = Assert.Single(Ndl.Lex(text).Diagnostics);

        Assert.Equal(code, diagnostic.Code);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.InRange(diagnostic.Span.Start, low: 0, text.Length);
        Assert.True(diagnostic.Span.Length > 0);
    }

    [Fact]
    public void MapsOffsetsToZeroBasedLinePositions()
    {
        SourceText source = new("one\r\ntwo\nthree");

        Assert.Equal(new LinePosition(Line: 0, Character: 2), source.GetLinePosition(position: 2));
        Assert.Equal(new LinePosition(Line: 1, Character: 1), source.GetLinePosition(position: 6));
        Assert.Equal(new LinePosition(Line: 2, Character: 0), source.GetLinePosition(position: 9));
    }
}