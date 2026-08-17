using System.Text.Json.Serialization;

namespace NativeDCB.Model;

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