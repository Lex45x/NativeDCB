namespace NativeDCB.Sdk;

public static class Decision
{
    public static DecisionModelBuilder<TCommand, EmptyDecisionModel> WithDecisionModel<TCommand>(TCommand command)
        where TCommand : notnull
    {
        ArgumentNullException.ThrowIfNull(command);
        return new DecisionModelBuilder<TCommand, EmptyDecisionModel>(new DecisionDefinitionState(command));
    }

    public static DecisionResultDefinition Accept(params object[] events)
    {
        return new AcceptedDecision(events);
    }

    public static DecisionResultDefinition Reject(string reason)
    {
        return new RejectedDecision(reason);
    }
}