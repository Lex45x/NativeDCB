namespace NativeDCB.Sdk;

public sealed record AcceptedDecision(
    // ReSharper disable once NotAccessedPositionalProperty.Global
    IReadOnlyList<object> Events) : DecisionResultDefinition;