namespace NativeDCB.Sdk.Decisions.Authoring;

public sealed record RejectedDecision(
    // ReSharper disable once NotAccessedPositionalProperty.Global
    string Reason) : DecisionResultDefinition;