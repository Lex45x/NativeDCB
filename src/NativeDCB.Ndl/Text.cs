namespace NativeDCB.Ndl;

public readonly record struct TextSpan(int Start, int Length)
{
    public int End => Start + Length;

    public static TextSpan FromBounds(int start, int end)
    {
        return new TextSpan(start, end - start);
    }
}

public readonly record struct LinePosition(int Line, int Character);

public sealed class SourceText
{
    private readonly int[] _lineStarts;

    public SourceText(string text)
    {
        Text = text ?? throw new ArgumentNullException(nameof(text));
        List<int> starts = [0];
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                i++;
            }

            if (text[i] is '\r' or '\n')
            {
                starts.Add(i + 1);
            }
        }

        _lineStarts = starts.ToArray();
    }

    public string Text { get; }
    public int Length => Text.Length;

    public LinePosition GetLinePosition(int position)
    {
        if (position < 0 || position > Text.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(position));
        }

        int line = Array.BinarySearch(_lineStarts, position);
        if (line < 0)
        {
            line = ~line - 1;
        }

        return new LinePosition(line, position - _lineStarts[line]);
    }

    public override string ToString()
    {
        return Text;
    }
}

public enum DiagnosticSeverity
{
    // ReSharper disable once UnusedMember.Global
    Warning,
    Error
}

public sealed record NdlDiagnostic(string Code, string Message, DiagnosticSeverity Severity, TextSpan Span);