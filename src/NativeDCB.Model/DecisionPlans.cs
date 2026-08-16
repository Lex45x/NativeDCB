using System.Text.Json.Serialization;

namespace NativeDCB.Model;

public sealed record DecisionPlan(
    string Name,
    string CommandSchema,
    string CommandAlias,
    IReadOnlyList<PlanInclude> Includes,
    IReadOnlyList<PlanEvaluationStep> Evaluation,
    IReadOnlyList<PlanEmission> Emissions,
    DecisionPlanFingerprints Fingerprints);

public sealed record DecisionPlanFingerprints(
    string LanguageVersion,
    string SourceFingerprint,
    IReadOnlyDictionary<string, string> SchemaFingerprints);

public sealed record PlanInclude(
    string EventType,
    string Alias,
    PlanExpression Where,
    IReadOnlyList<PlanKeyBinding> KeyBindings,
    IReadOnlyList<PlanAssignment> Assignments);

public sealed record PlanKeyBinding(string PropertyName, PlanExpression Value);

public sealed record PlanAssignment(string Name, PlanExpression Value);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$step")]
[JsonDerivedType(typeof(PlanRequirement), "require")]
[JsonDerivedType(typeof(PlanLocal), "let")]
public abstract record PlanEvaluationStep;

public sealed record PlanRequirement(PlanExpression Condition, PlanExpression Reason) : PlanEvaluationStep;

public sealed record PlanLocal(string Name, PlanExpression Value) : PlanEvaluationStep;

public sealed record PlanEmission(string EventType, IReadOnlyList<PlanAssignment> Assignments);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$expression")]
[JsonDerivedType(typeof(PlanLiteralExpression), "literal")]
[JsonDerivedType(typeof(PlanSymbolExpression), "symbol")]
[JsonDerivedType(typeof(PlanMemberExpression), "member")]
[JsonDerivedType(typeof(PlanCallExpression), "call")]
[JsonDerivedType(typeof(PlanUnaryExpression), "unary")]
[JsonDerivedType(typeof(PlanBinaryExpression), "binary")]
[JsonDerivedType(typeof(PlanConditionalExpression), "conditional")]
[JsonDerivedType(typeof(PlanObjectExpression), "object")]
public abstract record PlanExpression;

public sealed record PlanLiteralExpression(PlanLiteralKind Kind, string? Value) : PlanExpression;

public sealed record PlanSymbolExpression(string Name) : PlanExpression;

public sealed record PlanMemberExpression(PlanExpression Target, string Member) : PlanExpression;

public sealed record PlanCallExpression(string Function, IReadOnlyList<PlanExpression> Arguments) : PlanExpression;

public sealed record PlanUnaryExpression(PlanUnaryOperator Operator, PlanExpression Operand) : PlanExpression;

public sealed record PlanBinaryExpression(
    PlanExpression Left,
    PlanBinaryOperator Operator,
    PlanExpression Right) : PlanExpression;

public sealed record PlanConditionalExpression(
    PlanExpression Condition,
    PlanExpression WhenTrue,
    PlanExpression WhenFalse) : PlanExpression;

public sealed record PlanObjectExpression(IReadOnlyList<PlanAssignment> Assignments) : PlanExpression;

public enum PlanLiteralKind
{
    Null,
    Boolean,
    Integer,
    Decimal,
    String,
    Guid,
    DateTime,
    Duration
}

public enum PlanUnaryOperator
{
    Not,
    Plus,
    Minus
}

public enum PlanBinaryOperator
{
    Add,
    Subtract,
    Multiply,
    Divide,
    Remainder,
    Equal,
    NotEqual,
    Less,
    LessOrEqual,
    Greater,
    GreaterOrEqual,
    And,
    Or,
    Coalesce
}