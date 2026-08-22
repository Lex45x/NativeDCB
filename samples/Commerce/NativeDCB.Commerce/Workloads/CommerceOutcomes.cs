using NativeDCB.Protocol.V1;

namespace NativeDCB.Commerce.Workloads;

public enum CommerceSemanticOutcome
{
    Committed,
    Reconciled,
    Rejected,
    Stale,
    Failed
}

public sealed record CommerceOutcomeExpectation
{
    private readonly HashSet<CommerceSemanticOutcome> _allowed;

    public CommerceOutcomeExpectation(string name, params CommerceSemanticOutcome[] allowed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(allowed);
        if (allowed.Length == 0)
        {
            throw new ArgumentException("At least one semantic outcome is required.", nameof(allowed));
        }

        Name = name;
        _allowed = new HashSet<CommerceSemanticOutcome>(allowed);
        Allowed = _allowed.Order().ToArray();
    }

    public string Name { get; }

    public IReadOnlyList<CommerceSemanticOutcome> Allowed { get; }

    public bool Allows(CommerceSemanticOutcome outcome) => _allowed.Contains(outcome);
}

public static class CommerceExpectedOutcomes
{
    public static CommerceOutcomeExpectation IndependentWrite { get; } =
        new("independent-write", CommerceSemanticOutcome.Committed);

    public static CommerceOutcomeExpectation HotInventory { get; } =
        new("hot-inventory", CommerceSemanticOutcome.Committed, CommerceSemanticOutcome.Rejected);

    public static CommerceOutcomeExpectation ReservationVersusCheckout { get; } =
        new("reservation-versus-checkout", CommerceSemanticOutcome.Committed, CommerceSemanticOutcome.Rejected);

    public static CommerceOutcomeExpectation DuplicateCommand { get; } =
        new("duplicate-command", CommerceSemanticOutcome.Committed, CommerceSemanticOutcome.Reconciled);

    public static CommerceOutcomeExpectation RemoteCompletion { get; } =
        new("remote-completion", CommerceSemanticOutcome.Committed, CommerceSemanticOutcome.Stale);

    public static CommerceOutcomeExpectation FixturePopulation { get; } =
        new("fixture-population", CommerceSemanticOutcome.Committed, CommerceSemanticOutcome.Reconciled);
}

public static class CommerceOutcomes
{
    public static CommerceSemanticOutcome Classify(ExecuteHandlerResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return response.OutcomeCase switch
        {
            ExecuteHandlerResponse.OutcomeOneofCase.Committed => CommerceSemanticOutcome.Committed,
            ExecuteHandlerResponse.OutcomeOneofCase.AlreadyCommitted => CommerceSemanticOutcome.Reconciled,
            ExecuteHandlerResponse.OutcomeOneofCase.Rejected => CommerceSemanticOutcome.Rejected,
            ExecuteHandlerResponse.OutcomeOneofCase.Failed => CommerceSemanticOutcome.Failed,
            _ => CommerceSemanticOutcome.Failed
        };
    }

    public static CommerceSemanticOutcome Classify(CompleteDecisionResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return response.OutcomeCase switch
        {
            CompleteDecisionResponse.OutcomeOneofCase.Committed => CommerceSemanticOutcome.Committed,
            CompleteDecisionResponse.OutcomeOneofCase.AlreadyCommitted => CommerceSemanticOutcome.Reconciled,
            CompleteDecisionResponse.OutcomeOneofCase.Stale => CommerceSemanticOutcome.Stale,
            CompleteDecisionResponse.OutcomeOneofCase.Failed => CommerceSemanticOutcome.Failed,
            _ => CommerceSemanticOutcome.Failed
        };
    }
}
