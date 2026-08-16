namespace NativeDCB.Ndl;

public sealed class ParseResult(
    SourceText source,
    DocumentSyntax document,
    IReadOnlyList<NdlDiagnostic> diagnostics)
{
    public SourceText Source { get; } = source;
    public DocumentSyntax Document { get; } = document;
    public IReadOnlyList<NdlDiagnostic> Diagnostics { get; } = diagnostics;
    public bool HasErrors => Diagnostics.Any(value => value.Severity == DiagnosticSeverity.Error);
}

public static class NdlParser
{
    public static ParseResult Parse(string text)
    {
        LexResult lexed = NdlLexer.Lex(text);
        Parser parser = new(lexed);
        return parser.Parse();
    }

    private sealed class Parser
    {
        private readonly List<NdlDiagnostic> _diagnostics;
        private readonly LexResult _lexed;
        private readonly SyntaxToken[] _tokens;
        private int _position;

        public Parser(LexResult lexed)
        {
            _lexed = lexed;
            _tokens = lexed.Tokens.Where(x => x.Kind != SyntaxKind.BadToken).ToArray();
            _diagnostics = [.. lexed.Diagnostics];
        }

        private SyntaxToken Current => Peek(offset: 0);

        private SyntaxToken Peek(int offset)
        {
            return _tokens[Math.Min(_position + offset, _tokens.Length - 1)];
        }

        public ParseResult Parse()
        {
            List<DecisionSyntax> decisions = new();
            while (Current.Kind != SyntaxKind.EndOfFileToken)
            {
                if (Current.Kind == SyntaxKind.DecisionKeyword)
                {
                    decisions.Add(ParseDecision());
                    continue;
                }

                Report("NDL1001", $"Expected 'decision', but found {Display(Current)}.", Current.Span);
                NextToken();
            }

            TextSpan span = decisions.Count == 0
                ? new TextSpan(Start: 0, _lexed.Source.Length)
                : TextSpan.FromBounds(decisions[index: 0].Span.Start, decisions[^1].Span.End);
            return new ParseResult(_lexed.Source, new DocumentSyntax(decisions, span), _diagnostics);
        }

        private DecisionSyntax ParseDecision()
        {
            int start = Match(SyntaxKind.DecisionKeyword).Span.Start;
            SyntaxToken name = MatchIdentifier("decision name");
            FromClauseSyntax from = ParseFrom();
            List<IncludeStageSyntax> includes = new();
            while (Current.Kind == SyntaxKind.PipeToken && Peek(offset: 1).Kind == SyntaxKind.IncludeKeyword)
            {
                includes.Add(ParseInclude());
            }

            if (includes.Count == 0)
            {
                Report("NDL1101", "A decision must contain at least one include stage.", Current.Span);
            }

            EvaluateStageSyntax evaluate = ParseEvaluate();
            DecideStageSyntax decide = ParseDecide();
            SyntaxToken semicolon = Match(SyntaxKind.SemicolonToken);
            return new DecisionSyntax(name.Text, from, includes, evaluate, decide,
                TextSpan.FromBounds(start, semicolon.Span.End));
        }

        private FromClauseSyntax ParseFrom()
        {
            int start = Match(SyntaxKind.FromKeyword).Span.Start;
            (string Text, TextSpan Span) type = ParseTypeName();
            SyntaxToken alias = MatchIdentifier("command alias");
            return new FromClauseSyntax(type.Text, alias.Text, TextSpan.FromBounds(start, alias.Span.End));
        }

        private IncludeStageSyntax ParseInclude()
        {
            int start = Match(SyntaxKind.PipeToken).Span.Start;
            Match(SyntaxKind.IncludeKeyword);
            (string Text, TextSpan Span) type = ParseTypeName();
            SyntaxToken alias = MatchIdentifier("event alias");
            Match(SyntaxKind.WhereKeyword);
            ExpressionSyntax where = ParseExpression();
            Match(SyntaxKind.ApplyKeyword);
            ObjectExpressionSyntax apply = ParseObjectExpression();
            return new IncludeStageSyntax(type.Text, alias.Text, where, apply,
                TextSpan.FromBounds(start, apply.Span.End));
        }

        private EvaluateStageSyntax ParseEvaluate()
        {
            int start = Match(SyntaxKind.PipeToken).Span.Start;
            Match(SyntaxKind.EvaluateKeyword);
            Match(SyntaxKind.OpenBraceToken);
            List<EvaluateStatementSyntax> statements = new();
            while (Current.Kind is not (SyntaxKind.CloseBraceToken or SyntaxKind.EndOfFileToken))
            {
                int before = _position;
                if (Current.Kind == SyntaxKind.RequireKeyword)
                {
                    statements.Add(ParseRequire());
                }
                else if (Current.Kind == SyntaxKind.LetKeyword)
                {
                    statements.Add(ParseLet());
                }
                else
                {
                    Report("NDL1201", "Expected a 'require' or 'let' statement.", Current.Span);
                    RecoverStatement();
                }

                if (_position == before)
                {
                    NextToken();
                }
            }

            SyntaxToken close = Match(SyntaxKind.CloseBraceToken);
            return new EvaluateStageSyntax(statements, TextSpan.FromBounds(start, close.Span.End));
        }

        private RequireStatementSyntax ParseRequire()
        {
            int start = Match(SyntaxKind.RequireKeyword).Span.Start;
            ExpressionSyntax condition = ParseExpression();
            Match(SyntaxKind.ElseKeyword);
            ExpressionSyntax reason = ParseExpression();
            int end = Match(SyntaxKind.SemicolonToken).Span.End;
            return new RequireStatementSyntax(condition, reason, TextSpan.FromBounds(start, end));
        }

        private LetStatementSyntax ParseLet()
        {
            int start = Match(SyntaxKind.LetKeyword).Span.Start;
            SyntaxToken name = MatchIdentifier("binding name");
            Match(SyntaxKind.EqualsToken);
            ExpressionSyntax value = ParseExpression();
            int end = Match(SyntaxKind.SemicolonToken).Span.End;
            return new LetStatementSyntax(name.Text, value, TextSpan.FromBounds(start, end));
        }

        private DecideStageSyntax ParseDecide()
        {
            int start = Match(SyntaxKind.PipeToken).Span.Start;
            Match(SyntaxKind.DecideKeyword);
            Match(SyntaxKind.OpenBraceToken);
            List<EmitStatementSyntax> emissions = new();
            while (Current.Kind is not (SyntaxKind.CloseBraceToken or SyntaxKind.EndOfFileToken))
            {
                int before = _position;
                if (Current.Kind == SyntaxKind.EmitKeyword)
                {
                    emissions.Add(ParseEmit());
                }
                else
                {
                    Report("NDL1301", "Expected an 'emit' statement.", Current.Span);
                    RecoverStatement();
                }

                if (_position == before)
                {
                    NextToken();
                }
            }

            if (emissions.Count == 0)
            {
                Report("NDL1302", "A decide stage must emit at least one event.", Current.Span);
            }

            SyntaxToken close = Match(SyntaxKind.CloseBraceToken);
            return new DecideStageSyntax(emissions, TextSpan.FromBounds(start, close.Span.End));
        }

        private EmitStatementSyntax ParseEmit()
        {
            int start = Match(SyntaxKind.EmitKeyword).Span.Start;
            (string Text, TextSpan Span) type = ParseTypeName();
            ObjectExpressionSyntax value = ParseObjectExpression();
            int end = Match(SyntaxKind.SemicolonToken).Span.End;
            return new EmitStatementSyntax(type.Text, value, TextSpan.FromBounds(start, end));
        }

        private ObjectExpressionSyntax ParseObjectExpression()
        {
            int start = Match(SyntaxKind.OpenBraceToken).Span.Start;
            List<AssignmentSyntax> assignments = new();
            while (Current.Kind is not (SyntaxKind.CloseBraceToken or SyntaxKind.EndOfFileToken))
            {
                int assignmentStart = Current.Span.Start;
                SyntaxToken name = MatchIdentifier("property name");
                Match(SyntaxKind.EqualsToken);
                ExpressionSyntax value = ParseExpression();
                assignments.Add(new AssignmentSyntax(name.Text, value,
                    TextSpan.FromBounds(assignmentStart, value.Span.End)));

                if (Current.Kind != SyntaxKind.CommaToken)
                {
                    break;
                }

                NextToken();
                if (Current.Kind == SyntaxKind.CloseBraceToken)
                {
                    break;
                }
            }

            if (assignments.Count == 0)
            {
                Report("NDL1401", "An object must contain at least one assignment.", Current.Span);
            }

            SyntaxToken close = Match(SyntaxKind.CloseBraceToken);
            return new ObjectExpressionSyntax(assignments, TextSpan.FromBounds(start, close.Span.End));
        }

        private ExpressionSyntax ParseExpression(int parentPrecedence = 0)
        {
            ExpressionSyntax left;
            int unaryPrecedence = GetUnaryPrecedence(Current.Kind);
            if (unaryPrecedence != 0 && unaryPrecedence >= parentPrecedence)
            {
                SyntaxToken op = NextToken();
                ExpressionSyntax operand = ParseExpression(unaryPrecedence);
                left = new UnaryExpressionSyntax(op.Kind, operand,
                    TextSpan.FromBounds(op.Span.Start, operand.Span.End));
            }
            else
            {
                left = ParsePostfixExpression();
            }

            while (true)
            {
                int precedence = GetBinaryPrecedence(Current.Kind);
                if (precedence == 0 || precedence <= parentPrecedence)
                {
                    break;
                }

                SyntaxToken op = NextToken();
                int rightPrecedence = op.Kind == SyntaxKind.QuestionQuestionToken ? precedence - 1 : precedence;
                ExpressionSyntax right = ParseExpression(rightPrecedence);
                left = new BinaryExpressionSyntax(left, op.Kind, right,
                    TextSpan.FromBounds(left.Span.Start, right.Span.End));
            }

            if (parentPrecedence == 0 && Current.Kind == SyntaxKind.QuestionToken)
            {
                NextToken();
                ExpressionSyntax whenTrue = ParseExpression();
                Match(SyntaxKind.ColonToken);
                ExpressionSyntax whenFalse = ParseExpression();
                left = new ConditionalExpressionSyntax(left, whenTrue, whenFalse,
                    TextSpan.FromBounds(left.Span.Start, whenFalse.Span.End));
            }

            return left;
        }

        private ExpressionSyntax ParsePostfixExpression()
        {
            ExpressionSyntax expression = ParsePrimaryExpression();
            while (true)
            {
                if (Current.Kind == SyntaxKind.DotToken)
                {
                    NextToken();
                    SyntaxToken member = MatchIdentifier("member name");
                    expression = new MemberAccessExpressionSyntax(expression, member.Text,
                        TextSpan.FromBounds(expression.Span.Start, member.Span.End));
                    continue;
                }

                if (Current.Kind == SyntaxKind.OpenParenthesisToken)
                {
                    NextToken();
                    List<ExpressionSyntax> arguments = new();
                    while (Current.Kind is not (SyntaxKind.CloseParenthesisToken or SyntaxKind.EndOfFileToken))
                    {
                        arguments.Add(ParseExpression());
                        if (Current.Kind != SyntaxKind.CommaToken)
                        {
                            break;
                        }

                        NextToken();
                    }

                    SyntaxToken close = Match(SyntaxKind.CloseParenthesisToken);
                    expression = new CallExpressionSyntax(expression, arguments,
                        TextSpan.FromBounds(expression.Span.Start, close.Span.End));
                    continue;
                }

                break;
            }

            return expression;
        }

        private ExpressionSyntax ParsePrimaryExpression()
        {
            if (Current.Kind == SyntaxKind.OpenParenthesisToken)
            {
                SyntaxToken open = NextToken();
                ExpressionSyntax expression = ParseExpression();
                SyntaxToken close = Match(SyntaxKind.CloseParenthesisToken);
                return new ParenthesizedExpressionSyntax(expression,
                    TextSpan.FromBounds(open.Span.Start, close.Span.End));
            }

            if (Current.Kind == SyntaxKind.OpenBraceToken)
            {
                return ParseObjectExpression();
            }

            if (Current.Kind == SyntaxKind.IdentifierToken)
            {
                SyntaxToken identifier = NextToken();
                return new IdentifierExpressionSyntax(identifier.Text, identifier.Span);
            }

            LiteralKind? literalKind = GetLiteralKind(Current.Kind);
            if (literalKind is not null)
            {
                SyntaxToken token = NextToken();
                return new LiteralExpressionSyntax(literalKind.Value, token.Value, token.Text, token.Span);
            }

            Report("NDL1501", $"Expected an expression, but found {Display(Current)}.", Current.Span);
            TextSpan missing = new(Current.Span.Start, Length: 0);
            if (Current.Kind != SyntaxKind.EndOfFileToken)
            {
                NextToken();
            }

            return new IdentifierExpressionSyntax(string.Empty, missing);
        }

        private (string Text, TextSpan Span) ParseTypeName()
        {
            SyntaxToken first = MatchIdentifier("type name");
            string text = first.Text;
            int end = first.Span.End;
            while (Current.Kind == SyntaxKind.DotToken && Peek(offset: 1).Kind == SyntaxKind.IdentifierToken)
            {
                NextToken();
                SyntaxToken part = NextToken();
                text += "." + part.Text;
                end = part.Span.End;
            }

            return (text, TextSpan.FromBounds(first.Span.Start, end));
        }

        private SyntaxToken MatchIdentifier(string role)
        {
            if (Current.Kind == SyntaxKind.IdentifierToken)
            {
                return NextToken();
            }

            Report("NDL1003", $"Expected {role}, but found {Display(Current)}.", Current.Span);
            return Missing(SyntaxKind.IdentifierToken);
        }

        private SyntaxToken Match(SyntaxKind kind)
        {
            if (Current.Kind == kind)
            {
                return NextToken();
            }

            Report("NDL1002", $"Expected {Display(kind)}, but found {Display(Current)}.", Current.Span);
            return Missing(kind);
        }

        private SyntaxToken Missing(SyntaxKind kind)
        {
            return new SyntaxToken(kind, string.Empty, Value: null, new TextSpan(Current.Span.Start, Length: 0));
        }

        private SyntaxToken NextToken()
        {
            SyntaxToken current = Current;
            if (_position < _tokens.Length - 1)
            {
                _position++;
            }

            return current;
        }

        private void RecoverStatement()
        {
            while (Current.Kind is not (SyntaxKind.SemicolonToken or SyntaxKind.CloseBraceToken
                   or SyntaxKind.EndOfFileToken))
            {
                NextToken();
            }

            if (Current.Kind == SyntaxKind.SemicolonToken)
            {
                NextToken();
            }
        }

        private void Report(string code, string message, TextSpan span)
        {
            _diagnostics.Add(new NdlDiagnostic(code, message, DiagnosticSeverity.Error, span));
        }

        private static LiteralKind? GetLiteralKind(SyntaxKind kind)
        {
            return kind switch
            {
                SyntaxKind.NullKeyword => LiteralKind.Null,
                SyntaxKind.TrueKeyword or SyntaxKind.FalseKeyword => LiteralKind.Boolean,
                SyntaxKind.IntegerToken => LiteralKind.Integer,
                SyntaxKind.DecimalToken => LiteralKind.Decimal,
                SyntaxKind.StringToken => LiteralKind.String,
                SyntaxKind.GuidToken => LiteralKind.Guid,
                SyntaxKind.DateTimeToken => LiteralKind.DateTime,
                SyntaxKind.DurationToken => LiteralKind.Duration,
                _ => null
            };
        }

        private static int GetUnaryPrecedence(SyntaxKind kind)
        {
            return kind switch
            {
                SyntaxKind.NotKeyword or SyntaxKind.PlusToken or SyntaxKind.MinusToken => 8,
                _ => 0
            };
        }

        private static int GetBinaryPrecedence(SyntaxKind kind)
        {
            return kind switch
            {
                SyntaxKind.StarToken or SyntaxKind.SlashToken or SyntaxKind.PercentToken => 7,
                SyntaxKind.PlusToken or SyntaxKind.MinusToken => 6,
                SyntaxKind.LessToken or SyntaxKind.LessOrEqualsToken or SyntaxKind.GreaterToken
                    or SyntaxKind.GreaterOrEqualsToken => 5,
                SyntaxKind.EqualsEqualsToken or SyntaxKind.BangEqualsToken => 4,
                SyntaxKind.AndKeyword => 3,
                SyntaxKind.OrKeyword => 2,
                SyntaxKind.QuestionQuestionToken => 1,
                _ => 0
            };
        }

        private static string Display(SyntaxToken token)
        {
            return token.Kind == SyntaxKind.EndOfFileToken ? "end of file" : $"'{token.Text}'";
        }

        private static string Display(SyntaxKind kind)
        {
            return kind switch
            {
                SyntaxKind.PipeToken => "'|'",
                SyntaxKind.OpenBraceToken => "'{'",
                SyntaxKind.CloseBraceToken => "'}'",
                SyntaxKind.SemicolonToken => "';'",
                SyntaxKind.EqualsToken => "'='",
                SyntaxKind.CloseParenthesisToken => "')'",
                _ when kind.ToString().EndsWith("Keyword", StringComparison.Ordinal) =>
                    $"'{kind.ToString()[..^7].ToLowerInvariant()}'",
                _ => kind.ToString()
            };
        }
    }
}