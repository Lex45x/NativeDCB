namespace NativeDCB.Sdk;

public sealed record RejectedDecision(
    // ReSharper disable once NotAccessedPositionalProperty.Global
    string Reason) : DecisionResultDefinition;