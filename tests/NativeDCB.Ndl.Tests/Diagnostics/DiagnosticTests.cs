using NativeDCB.Ndl.Diagnostics;
using NativeDCB.Ndl.Parsing;
using NativeDCB.Ndl.Syntax;
using NativeDCB.Ndl.Syntax.Statements;
using NativeDCB.Ndl.Tests.Support;

namespace NativeDCB.Ndl.Tests.Diagnostics;

public class DiagnosticTests
{
    [Fact]
    public void ReportsMissingIncludeWithoutThrowing()
    {
        const string source = "decision Empty from Command command | evaluate { } | decide { emit Event { Id = 1 }; };";

        ParseResult result = Ndl.Parse(source);

        Assert.Contains(result.Diagnostics,
            value => value is { Code: "NDL1101", Severity: DiagnosticSeverity.Error });
        Assert.Single(result.Document.Decisions);
    }

    [Fact]
    public void ReportsMissingRequiredTokensAtInsertionPoint()
    {
        string source = TestSources.DecisionWithExpression("1").Replace("else", "else");
        source = source.Replace("from Command command", "from Command");

        ParseResult result = Ndl.Parse(source);

        NdlDiagnostic diagnostic = Assert.Single(result.Diagnostics, x => x.Code == "NDL1003");
        Assert.Equal(expected: 0, result.Document.Decisions[index: 0].From.Alias.Length);
        Assert.InRange(diagnostic.Span.Start, low: 0, source.Length);
    }

    [Fact]
    public void ReportsInvalidEvaluateStatementAndRecoversAtSemicolon()
    {
        string source = TestSources.DecisionWithExpression("1")
            .Replace("| evaluate { }", "| evaluate { unknown value; let valid = 2; }");

        ParseResult result = Ndl.Parse(source);

        Assert.Contains(result.Diagnostics, x => x.Code == "NDL1201");
        EvaluateStageSyntax evaluate = result.Document.Decisions[index: 0].Evaluate;
        Assert.Single(evaluate.Statements);
        Assert.IsType<LetStatementSyntax>(evaluate.Statements[index: 0]);
    }

    [Fact]
    public void ReportsEmptyObjectAndEmptyDecide()
    {
        const string source =
            "decision Empty from Command command | include Event event where event.Id == command.Id apply { } | evaluate { } | decide { };";

        ParseResult result = Ndl.Parse(source);

        Assert.Contains(result.Diagnostics, x => x.Code == "NDL1401");
        Assert.Contains(result.Diagnostics, x => x.Code == "NDL1302");
    }

    [Fact]
    public void ReportsUnexpectedTopLevelInputAndContinues()
    {
        string source = "garbage " + TestSources.DecisionWithExpression("1");

        ParseResult result = Ndl.Parse(source);

        Assert.Contains(result.Diagnostics, x => x.Code == "NDL1001");
        Assert.Single(result.Document.Decisions);
    }

    [Fact]
    public void CarriesLexerDiagnosticsIntoParseResult()
    {
        ParseResult result = Ndl.Parse(TestSources.DecisionWithExpression("@"));

        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, x => x.Code == "NDL0001");
        Assert.Contains(result.Diagnostics, x => x.Code == "NDL1501");
    }
}