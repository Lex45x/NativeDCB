namespace NativeDCB.Sdk.Decisions.Authoring;

public sealed record AcceptedDecision(
    // ReSharper disable once NotAccessedPositionalProperty.Global
    IReadOnlyList<object> Events) : DecisionResultDefinition;