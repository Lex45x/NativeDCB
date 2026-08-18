using NativeDCB.Ndl.Text;

namespace NativeDCB.Ndl.Lexing;

public sealed record SyntaxToken(SyntaxKind Kind, string Text, object? Value, TextSpan Span);