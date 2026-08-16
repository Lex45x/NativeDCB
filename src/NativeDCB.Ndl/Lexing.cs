using System.Globalization;
using System.Text;

namespace NativeDCB.Ndl;

public enum SyntaxKind
{
    BadToken,
    EndOfFileToken,
    IdentifierToken,
    IntegerToken,
    DecimalToken,
    StringToken,
    GuidToken,
    DateTimeToken,
    DurationToken,
    DecisionKeyword,
    FromKeyword,
    IncludeKeyword,
    WhereKeyword,
    ApplyKeyword,
    EvaluateKeyword,
    RequireKeyword,
    ElseKeyword,
    LetKeyword,
    DecideKeyword,
    EmitKeyword,
    AndKeyword,
    OrKeyword,
    NotKeyword,
    TrueKeyword,
    FalseKeyword,
    NullKeyword,
    OpenBraceToken,
    CloseBraceToken,
    OpenParenthesisToken,
    CloseParenthesisToken,
    CommaToken,
    DotToken,
    SemicolonToken,
    PipeToken,
    PlusToken,
    MinusToken,
    StarToken,
    SlashToken,
    PercentToken,
    EqualsToken,
    EqualsEqualsToken,
    BangEqualsToken,
    LessToken,
    LessOrEqualsToken,
    GreaterToken,
    GreaterOrEqualsToken,
    QuestionToken,
    ColonToken,
    QuestionQuestionToken
}

public sealed record SyntaxToken(SyntaxKind Kind, string Text, object? Value, TextSpan Span);

public sealed class LexResult(
    SourceText source,
    IReadOnlyList<SyntaxToken> tokens,
    IReadOnlyList<NdlDiagnostic> diagnostics)
{
    public SourceText Source { get; } = source;
    public IReadOnlyList<SyntaxToken> Tokens { get; } = tokens;
    public IReadOnlyList<NdlDiagnostic> Diagnostics { get; } = diagnostics;

    // ReSharper disable once UnusedMember.Global
    public bool HasErrors => Diagnostics.Any(value => value.Severity == DiagnosticSeverity.Error);
}

public static class NdlLexer
{
    private static readonly IReadOnlyDictionary<string, SyntaxKind> Keywords =
        new Dictionary<string, SyntaxKind>(StringComparer.Ordinal)
        {
            ["decision"] = SyntaxKind.DecisionKeyword,
            ["from"] = SyntaxKind.FromKeyword,
            ["include"] = SyntaxKind.IncludeKeyword,
            ["where"] = SyntaxKind.WhereKeyword,
            ["apply"] = SyntaxKind.ApplyKeyword,
            ["evaluate"] = SyntaxKind.EvaluateKeyword,
            ["require"] = SyntaxKind.RequireKeyword,
            ["else"] = SyntaxKind.ElseKeyword,
            ["let"] = SyntaxKind.LetKeyword,
            ["decide"] = SyntaxKind.DecideKeyword,
            ["emit"] = SyntaxKind.EmitKeyword,
            ["and"] = SyntaxKind.AndKeyword,
            ["or"] = SyntaxKind.OrKeyword,
            ["not"] = SyntaxKind.NotKeyword,
            ["true"] = SyntaxKind.TrueKeyword,
            ["false"] = SyntaxKind.FalseKeyword,
            ["null"] = SyntaxKind.NullKeyword
        };

    public static LexResult Lex(string text)
    {
        SourceText source = new(text);
        Lexer lexer = new(source);
        return lexer.Lex();
    }

    private sealed class Lexer(SourceText source)
    {
        private readonly List<NdlDiagnostic> _diagnostics = [];
        private readonly List<SyntaxToken> _tokens = [];
        private int _position;

        private char Current => Peek(offset: 0);

        private char Peek(int offset)
        {
            return _position + offset < source.Length ? source.Text[_position + offset] : '\0';
        }

        public LexResult Lex()
        {
            while (_position < source.Length)
            {
                SkipTrivia();
                if (_position >= source.Length)
                {
                    break;
                }

                LexToken();
            }

            _tokens.Add(new SyntaxToken(SyntaxKind.EndOfFileToken, string.Empty, Value: null,
                new TextSpan(source.Length, Length: 0)));
            return new LexResult(source, _tokens, _diagnostics);
        }

        private void SkipTrivia()
        {
            while (_position < source.Length)
            {
                if (char.IsWhiteSpace(Current))
                {
                    _position++;
                    continue;
                }

                if (Current == '/' && Peek(offset: 1) == '/')
                {
                    _position += 2;
                    while (Current is not ('\r' or '\n' or '\0'))
                    {
                        _position++;
                    }

                    continue;
                }

                if (Current == '/' && Peek(offset: 1) == '*')
                {
                    int start = _position;
                    _position += 2;
                    while (_position < source.Length && !(Current == '*' && Peek(offset: 1) == '/'))
                    {
                        _position++;
                    }

                    if (_position >= source.Length)
                    {
                        Report("NDL0002", "Unterminated block comment.", TextSpan.FromBounds(start, source.Length));
                        return;
                    }

                    _position += 2;
                    continue;
                }

                break;
            }
        }

        private void LexToken()
        {
            int start = _position;
            if (TryLexGuid(start))
            {
                return;
            }

            if (char.IsLetter(Current) || Current == '_')
            {
                _position++;
                while (char.IsLetterOrDigit(Current) || Current == '_')
                {
                    _position++;
                }

                string text = Slice(start);
                SyntaxKind kind = Keywords.GetValueOrDefault(text, SyntaxKind.IdentifierToken);
                object? value = kind switch
                {
                    SyntaxKind.TrueKeyword => true,
                    SyntaxKind.FalseKeyword => false,
                    _ => null
                };
                Add(kind, start, value);
                return;
            }

            if (char.IsDigit(Current))
            {
                LexNumberOrTypedLiteral(start);
                return;
            }

            if (Current == '"')
            {
                LexString(start);
                return;
            }

            switch (Current)
            {
                case '{': AddSingle(SyntaxKind.OpenBraceToken, start); break;
                case '}': AddSingle(SyntaxKind.CloseBraceToken, start); break;
                case '(': AddSingle(SyntaxKind.OpenParenthesisToken, start); break;
                case ')': AddSingle(SyntaxKind.CloseParenthesisToken, start); break;
                case ',': AddSingle(SyntaxKind.CommaToken, start); break;
                case '.': AddSingle(SyntaxKind.DotToken, start); break;
                case ';': AddSingle(SyntaxKind.SemicolonToken, start); break;
                case '|': AddSingle(SyntaxKind.PipeToken, start); break;
                case '+': AddSingle(SyntaxKind.PlusToken, start); break;
                case '-': AddSingle(SyntaxKind.MinusToken, start); break;
                case '*': AddSingle(SyntaxKind.StarToken, start); break;
                case '/': AddSingle(SyntaxKind.SlashToken, start); break;
                case '%': AddSingle(SyntaxKind.PercentToken, start); break;
                case ':': AddSingle(SyntaxKind.ColonToken, start); break;
                case '=' when Peek(offset: 1) == '=': AddDouble(SyntaxKind.EqualsEqualsToken, start); break;
                case '=': AddSingle(SyntaxKind.EqualsToken, start); break;
                case '!' when Peek(offset: 1) == '=': AddDouble(SyntaxKind.BangEqualsToken, start); break;
                case '<' when Peek(offset: 1) == '=': AddDouble(SyntaxKind.LessOrEqualsToken, start); break;
                case '<': AddSingle(SyntaxKind.LessToken, start); break;
                case '>' when Peek(offset: 1) == '=': AddDouble(SyntaxKind.GreaterOrEqualsToken, start); break;
                case '>': AddSingle(SyntaxKind.GreaterToken, start); break;
                case '?' when Peek(offset: 1) == '?': AddDouble(SyntaxKind.QuestionQuestionToken, start); break;
                case '?': AddSingle(SyntaxKind.QuestionToken, start); break;
                default:
                    _position++;
                    Add(SyntaxKind.BadToken, start);
                    Report("NDL0001", $"Unexpected character '{source.Text[start]}'.", new TextSpan(start, Length: 1));
                    break;
            }
        }

        private void LexNumberOrTypedLiteral(int start)
        {
            if (Peek(offset: 4) == '-' && Peek(offset: 7) == '-')
            {
                while (char.IsLetterOrDigit(Current) || Current is '-' or ':' or '+' or '.')
                {
                    _position++;
                }

                string candidate = Slice(start);
                if (DateTimeOffset.TryParse(candidate, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind,
                        out DateTimeOffset dateTime))
                {
                    Add(SyntaxKind.DateTimeToken, start, dateTime);
                    return;
                }

                _position = start;
            }

            while (char.IsDigit(Current))
            {
                _position++;
            }

            bool isDecimal = false;
            if (Current == '.' && char.IsDigit(Peek(offset: 1)))
            {
                isDecimal = true;
                _position++;
                while (char.IsDigit(Current))
                {
                    _position++;
                }
            }

            int numberEnd = _position;
            while (char.IsLetter(Current))
            {
                _position++;
            }

            if (_position > numberEnd)
            {
                string unit = source.Text[numberEnd.._position];
                string numberText = source.Text[start..numberEnd];
                if (decimal.TryParse(numberText, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture,
                        out decimal amount)
                    && TryDuration(amount, unit, out TimeSpan duration))
                {
                    Add(SyntaxKind.DurationToken, start, duration);
                    return;
                }

                Report("NDL0005", $"Unknown duration suffix '{unit}'.", TextSpan.FromBounds(numberEnd, _position));
                Add(SyntaxKind.BadToken, start);
                return;
            }

            string text = Slice(start);
            if (isDecimal)
            {
                if (decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture,
                        out decimal value))
                {
                    Add(SyntaxKind.DecimalToken, start, value);
                }
                else
                {
                    InvalidNumber(start);
                }
            }
            else if (long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long value))
            {
                Add(SyntaxKind.IntegerToken, start, value);
            }
            else
            {
                InvalidNumber(start);
            }
        }

        private void LexString(int start)
        {
            _position++;
            StringBuilder value = new();
            bool terminated = false;
            while (_position < source.Length)
            {
                char c = Current;
                if (c == '"')
                {
                    _position++;
                    terminated = true;
                    break;
                }

                if (c is '\r' or '\n')
                {
                    break;
                }

                if (c == '\\')
                {
                    _position++;
                    if (_position >= source.Length)
                    {
                        break;
                    }

                    c = Current;
                    value.Append(c switch
                    {
                        'n' => '\n',
                        'r' => '\r',
                        't' => '\t',
                        '"' => '"',
                        '\\' => '\\',
                        _ => c
                    });
                    if (c is not ('n' or 'r' or 't' or '"' or '\\'))
                    {
                        Report("NDL0004", $"Unknown escape sequence '\\{c}'.", new TextSpan(_position - 1, Length: 2));
                    }

                    _position++;
                    continue;
                }

                value.Append(c);
                _position++;
            }

            if (!terminated)
            {
                Report("NDL0003", "Unterminated string literal.", TextSpan.FromBounds(start, _position));
            }

            Add(SyntaxKind.StringToken, start, value.ToString());
        }

        private static bool TryDuration(decimal amount, string unit, out TimeSpan result)
        {
            try
            {
                result = unit switch
                {
                    "ms" => TimeSpan.FromMilliseconds((double)amount),
                    "s" => TimeSpan.FromSeconds((double)amount),
                    "m" => TimeSpan.FromMinutes((double)amount),
                    "h" => TimeSpan.FromHours((double)amount),
                    "d" => TimeSpan.FromDays((double)amount),
                    _ => TimeSpan.Zero
                };
                return unit is "ms" or "s" or "m" or "h" or "d";
            }
            catch (OverflowException)
            {
                result = TimeSpan.Zero;
                return false;
            }
        }

        private bool TryLexGuid(int start)
        {
            if (start + 36 > source.Length)
            {
                return false;
            }

            string candidate = source.Text.Substring(start, length: 36);
            if (!Guid.TryParseExact(candidate, "D", out Guid guid) || !IsBoundary(Peek(offset: 36)))
            {
                return false;
            }

            _position += 36;
            Add(SyntaxKind.GuidToken, start, guid);
            return true;
        }

        private bool IsBoundary(char c)
        {
            return !(char.IsLetterOrDigit(c) || c == '_');
        }

        private string Slice(int start)
        {
            return source.Text[start.._position];
        }

        private void InvalidNumber(int start)
        {
            Add(SyntaxKind.BadToken, start);
            Report("NDL0006", "Numeric literal is outside the supported range.", TextSpan.FromBounds(start, _position));
        }

        private void AddSingle(SyntaxKind kind, int start)
        {
            _position++;
            Add(kind, start);
        }

        private void AddDouble(SyntaxKind kind, int start)
        {
            _position += 2;
            Add(kind, start);
        }

        private void Add(SyntaxKind kind, int start, object? value = null)
        {
            _tokens.Add(new SyntaxToken(kind, source.Text[start.._position], value,
                TextSpan.FromBounds(start, _position)));
        }

        private void Report(string code, string message, TextSpan span)
        {
            _diagnostics.Add(new NdlDiagnostic(code, message, DiagnosticSeverity.Error, span));
        }
    }
}