using NativeDCB.Model;

namespace NativeDCB.Ndl;

public static class Ndl
{
    public static LexResult Lex(string text)
    {
        return NdlLexer.Lex(text);
    }

    public static ParseResult Parse(string text)
    {
        return NdlParser.Parse(text);
    }

    public static CompilationResult Compile(string text)
    {
        return NdlCompiler.Compile(text);
    }

    public static string Format(string text)
    {
        return NdlFormatter.Format(text);
    }

    public static string Format(DocumentSyntax document)
    {
        return NdlFormatter.Format(document);
    }

    public static string Format(DecisionPlan plan)
    {
        return NdlPlanFormatter.Format(plan);
    }

    public static NdlPlanFormatResult TryFormat(DecisionPlan plan)
    {
        return NdlPlanFormatter.TryFormat(plan);
    }
}