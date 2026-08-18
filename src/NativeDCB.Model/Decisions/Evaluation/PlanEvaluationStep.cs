using System.Text.Json.Serialization;

namespace NativeDCB.Model.Decisions.Evaluation;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$step")]
[JsonDerivedType(typeof(PlanRequirement), "require")]
[JsonDerivedType(typeof(PlanLocal), "let")]
public abstract record PlanEvaluationStep;