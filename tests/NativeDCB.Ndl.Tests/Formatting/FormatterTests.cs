using NativeDCB.Ndl.Formatting;
using NativeDCB.Ndl.Parsing;
using NativeDCB.Ndl.Tests.Support;

namespace NativeDCB.Ndl.Tests.Formatting;

public class FormatterTests
{
    [Fact]
    public void FormatsDecisionCanonically()
    {
        const string source =
            "decision D from C c | include E e where e.Id==c.Id apply {Count=(previous.Count??0)+1, Active=true}| evaluate {require not model.Blocked else \"no\";let n=model.Count+1;}| decide {emit R {N=n};};";

        string formatted = Ndl.Format(source);

        Assert.Equal(expected: """
                               decision D
                               from C c
                               | include E e
                                   where e.Id == c.Id
                                   apply {
                                       Count = (previous.Count ?? 0) + 1,
                                       Active = true
                                   }
                               | evaluate {
                                   require not model.Blocked
                                       else "no";
                                   let n = model.Count + 1;
                               }
                               | decide {
                                   emit R {
                                       N = n
                                   };
                               };

                               """, formatted);
    }

    [Fact]
    public void FormattingIsIdempotentAndReparseable()
    {
        string first = Ndl.Format(TestSources.Complete);
        string second = Ndl.Format(first);
        ParseResult parsed = Ndl.Parse(second);

        Assert.Equal(first, second);
        Assert.Empty(parsed.Diagnostics);
        Assert.Equal(expected: 2, parsed.Document.Decisions[index: 0].Decide.Emissions.Count);
    }

    [Theory]
    [InlineData("a + b * c", "a + b * c")]
    [InlineData("(a + b) * c", "(a + b) * c")]
    [InlineData("a - (b - c)", "a - (b - c)")]
    [InlineData("not (a or b)", "not (a or b)")]
    [InlineData("a ?? b ?? c", "a ?? b ?? c")]
    [InlineData("(a ?? b) ?? c", "(a ?? b) ?? c")]
    [InlineData("(a ? b : c) ? d : e", "(a ? b : c) ? d : e")]
    [InlineData("1.0", "1.0")]
    public void PreservesExpressionMeaning(string expression, string expected)
    {
        string formatted = Ndl.Format(TestSources.DecisionWithExpression(expression));

        Assert.Contains("Value = " + expected, formatted, StringComparison.Ordinal);
        Assert.Empty(Ndl.Parse(formatted).Diagnostics);
    }

    [Fact]
    public void EscapesStringValuesCanonically()
    {
        string formatted = Ndl.Format(TestSources.DecisionWithExpression("\"a\\n\\\"b\\\"\""));

        Assert.Contains("Value = \"a\\n\\\"b\\\"\"", formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesToFormatInvalidSource()
    {
        NdlFormatException exception = Assert.Throws<NdlFormatException>(() => Ndl.Format("not a decision"));

        Assert.NotEmpty(exception.Diagnostics);
    }
}