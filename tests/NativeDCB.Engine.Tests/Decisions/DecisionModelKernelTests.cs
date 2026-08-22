using System.Text.Json;

using NativeDCB.Actors.Decisions.Execution;
using NativeDCB.Model.Decisions;
using NativeDCB.Model.Decisions.Evaluation;
using NativeDCB.Model.Decisions.Expressions;
using NativeDCB.Model.Events;

namespace NativeDCB.Engine.Tests.Decisions;

public sealed class DecisionModelKernelTests
{
    [Fact]
    public void EvaluatesAllAssignmentsAgainstTheSamePreIncludeSnapshot()
    {
        PlanExpression currentFromModel = new PlanBinaryExpression(
            new PlanMemberExpression(new PlanSymbolExpression("model"), "current"),
            PlanBinaryOperator.Coalesce,
            new PlanLiteralExpression(PlanLiteralKind.Integer, "0"));
        PlanExpression currentFromPrevious = new PlanBinaryExpression(
            new PlanMemberExpression(new PlanSymbolExpression("previous"), "current"),
            PlanBinaryOperator.Coalesce,
            new PlanLiteralExpression(PlanLiteralKind.Integer, "0"));
        DecisionPlan plan = Plan() with
        {
            Includes =
            [
                new PlanInclude(
                    "Applied",
                    "applied",
                    new PlanLiteralExpression(PlanLiteralKind.Boolean, "true"),
                    [],
                    [
                        new PlanAssignment(
                            "current",
                            new PlanMemberExpression(new PlanSymbolExpression("applied"), "amount")),
                        new PlanAssignment("modelCurrent", currentFromModel),
                        new PlanAssignment("previousCurrent", currentFromPrevious)
                    ])
            ]
        };

        Dictionary<string, object?> model = DecisionModelKernel.Replay(
            plan,
            JsonSerializer.SerializeToElement(new { }),
            [Event(1, "Applied", JsonSerializer.SerializeToElement(new { amount = 4 }))],
            ensureInitialized: true);

        Assert.Equal(4L, model["current"]);
        Assert.Equal(0L, model["modelCurrent"]);
        Assert.Equal(0L, model["previousCurrent"]);
    }

    [Fact]
    public void ReplaysPreviousStateAndEvaluatesAcceptedAndRejectedDecisions()
    {
        DecisionPlan plan = Plan();
        SequencedEvent[] history =
        [
            Event(eventId: 1, amount: 4),
            Event(eventId: 2, amount: 7)
        ];
        Dictionary<string, object?> model = DecisionModelKernel.Replay(
            plan,
            JsonSerializer.SerializeToElement(new { amount = 0, limit = 100 }),
            history,
            ensureInitialized: true);

        Assert.Equal(11m, model["balance"]);

        DecisionModelEvaluationResult accepted = DecisionModelKernel.Evaluate(
            plan,
            JsonSerializer.SerializeToElement(new { amount = 1, limit = 12 }),
            model);
        Assert.False(accepted.Rejected);
        EvaluatedDecisionEmission emission = Assert.Single(accepted.Emissions);
        Assert.Equal("BalanceChanged", emission.EventType);
        Assert.Equal(12m, emission.Payload["balance"]);

        DecisionModelEvaluationResult rejected = DecisionModelKernel.Evaluate(
            plan,
            JsonSerializer.SerializeToElement(new { amount = 2, limit = 12 }),
            model);
        Assert.True(rejected.Rejected);
        Assert.Equal("Balance limit exceeded", rejected.RejectionReason);
        Assert.Empty(rejected.Emissions);
    }

    [Fact]
    public void ReplayFiltersEventsAndRunsMultipleMatchingIncludesInSourceOrder()
    {
        PlanExpression total = new PlanBinaryExpression(
            new PlanBinaryExpression(
                new PlanMemberExpression(new PlanSymbolExpression("previous"), "total"),
                PlanBinaryOperator.Coalesce,
                new PlanLiteralExpression(PlanLiteralKind.Integer, "0")),
            PlanBinaryOperator.Add,
            new PlanMemberExpression(new PlanSymbolExpression("deposit"), "amount"));
        PlanExpression bonuses = new PlanBinaryExpression(
            new PlanBinaryExpression(
                new PlanMemberExpression(new PlanSymbolExpression("previous"), "bonuses"),
                PlanBinaryOperator.Coalesce,
                new PlanLiteralExpression(PlanLiteralKind.Integer, "0")),
            PlanBinaryOperator.Add,
            new PlanLiteralExpression(PlanLiteralKind.Integer, "1"));
        PlanExpression afterFee = new PlanBinaryExpression(
            new PlanMemberExpression(new PlanSymbolExpression("previous"), "total"),
            PlanBinaryOperator.Subtract,
            new PlanMemberExpression(new PlanSymbolExpression("fee"), "amount"));
        DecisionPlan plan = Plan() with
        {
            Includes =
            [
                new PlanInclude(
                    "Deposited",
                    "deposit",
                    new PlanBinaryExpression(
                        new PlanMemberExpression(new PlanSymbolExpression("deposit"), "account"),
                        PlanBinaryOperator.Equal,
                        new PlanMemberExpression(new PlanSymbolExpression("command"), "account")),
                    [],
                    [new PlanAssignment("total", total)]),
                new PlanInclude(
                    "Deposited",
                    "deposit",
                    new PlanMemberExpression(new PlanSymbolExpression("deposit"), "bonus"),
                    [],
                    [
                        new PlanAssignment("bonuses", bonuses),
                        new PlanAssignment(
                            "totalAtBonus",
                            new PlanMemberExpression(new PlanSymbolExpression("previous"), "total"))
                    ]),
                new PlanInclude(
                    "FeeCharged",
                    "fee",
                    new PlanLiteralExpression(PlanLiteralKind.Boolean, "true"),
                    [],
                    [new PlanAssignment("total", afterFee)])
            ]
        };
        SequencedEvent[] history =
        [
            Event(1, "Deposited", JsonSerializer.SerializeToElement(new { account = "other", amount = 100, bonus = false })),
            Event(2, "Deposited", JsonSerializer.SerializeToElement(new { account = "a-1", amount = 10, bonus = true })),
            Event(3, "FeeCharged", JsonSerializer.SerializeToElement(new { amount = 3 })),
            Event(4, "Deposited", JsonSerializer.SerializeToElement(new { account = "a-1", amount = 5, bonus = false }))
        ];

        Dictionary<string, object?> model = DecisionModelKernel.Replay(
            plan,
            JsonSerializer.SerializeToElement(new { account = "a-1" }),
            history,
            ensureInitialized: true);

        Assert.Equal(12m, model["total"]);
        Assert.Equal(1m, model["bonuses"]);
        Assert.Equal(10m, model["totalAtBonus"]);
    }

    [Fact]
    public void ReplayValidatesInitializationOnlyWhenRequested()
    {
        DecisionPlan plan = Plan() with
        {
            Includes =
            [
                new PlanInclude(
                    "Applied",
                    "applied",
                    new PlanLiteralExpression(PlanLiteralKind.Boolean, "true"),
                    [],
                    [
                        new PlanAssignment(
                            "nested",
                            new PlanObjectExpression(
                                [new PlanAssignment("value", new PlanSymbolExpression("uninitialized"))]))
                    ])
            ]
        };
        SequencedEvent[] history =
        [
            Event(1, "Applied", JsonSerializer.SerializeToElement(new { }))
        ];

        Dictionary<string, object?> model = DecisionModelKernel.Replay(
            plan,
            JsonSerializer.SerializeToElement(new { }),
            history,
            ensureInitialized: false);
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            DecisionModelKernel.Replay(
                plan,
                JsonSerializer.SerializeToElement(new { }),
                history,
                ensureInitialized: true));

        Assert.Contains("nested", model.Keys);
        Assert.Equal("The prepared decision model contains an uninitialized value.", exception.Message);
    }

    [Fact]
    public void EvaluationStopsAtTheFirstRejectionAfterApplyingPriorLocals()
    {
        DecisionPlan plan = Plan() with
        {
            Evaluation =
            [
                new PlanRequirement(
                    new PlanLiteralExpression(PlanLiteralKind.Boolean, "true"),
                    new PlanLiteralExpression(PlanLiteralKind.String, "not used")),
                new PlanLocal("reason", new PlanLiteralExpression(PlanLiteralKind.String, "second requirement")),
                new PlanRequirement(
                    new PlanLiteralExpression(PlanLiteralKind.Boolean, "false"),
                    new PlanSymbolExpression("reason")),
                new PlanLocal(
                    "unreachable",
                    new PlanBinaryExpression(
                        new PlanSymbolExpression("missing"),
                        PlanBinaryOperator.Add,
                        new PlanLiteralExpression(PlanLiteralKind.Integer, "1")))
            ]
        };

        DecisionModelEvaluationResult result = DecisionModelKernel.Evaluate(
            plan,
            JsonSerializer.SerializeToElement(new { }),
            new Dictionary<string, object?>());

        Assert.True(result.Rejected);
        Assert.Equal("second requirement", result.RejectionReason);
        Assert.Empty(result.Emissions);
    }

    private static DecisionPlan Plan()
    {
        PlanExpression previousBalance = new PlanMemberExpression(new PlanSymbolExpression("previous"), "balance");
        PlanExpression eventAmount = new PlanMemberExpression(new PlanSymbolExpression("deposit"), "amount");
        PlanExpression replayedBalance = new PlanBinaryExpression(
            new PlanBinaryExpression(
                previousBalance,
                PlanBinaryOperator.Coalesce,
                new PlanLiteralExpression(PlanLiteralKind.Integer, "0")),
            PlanBinaryOperator.Add,
            eventAmount);
        PlanExpression projected = new PlanBinaryExpression(
            new PlanMemberExpression(new PlanSymbolExpression("model"), "balance"),
            PlanBinaryOperator.Add,
            new PlanMemberExpression(new PlanSymbolExpression("command"), "amount"));

        return new DecisionPlan(
            "ChangeBalance",
            "ChangeBalance",
            "command",
            [
                new PlanInclude(
                    "Deposited",
                    "deposit",
                    new PlanLiteralExpression(PlanLiteralKind.Boolean, "true"),
                    [],
                    [new PlanAssignment("balance", replayedBalance)])
            ],
            [
                new PlanLocal("projected", projected),
                new PlanRequirement(
                    new PlanBinaryExpression(
                        new PlanSymbolExpression("projected"),
                        PlanBinaryOperator.LessOrEqual,
                        new PlanMemberExpression(new PlanSymbolExpression("command"), "limit")),
                    new PlanLiteralExpression(PlanLiteralKind.String, "Balance limit exceeded"))
            ],
            [
                new PlanEmission(
                    "BalanceChanged",
                    [new PlanAssignment("balance", new PlanSymbolExpression("projected"))])
            ],
            new DecisionPlanFingerprints("test", "test", new Dictionary<string, string>()));
    }

    private static SequencedEvent Event(long eventId, long amount)
    {
        return Event(eventId, "Deposited", JsonSerializer.SerializeToElement(new { amount }));
    }

    private static SequencedEvent Event(long eventId, string type, JsonElement data)
    {
        return new SequencedEvent(
            eventId,
            type,
            data,
            [],
            SchemaVersion: 1,
            DateTimeOffset.UnixEpoch,
            Guid.Empty,
            "Deposit");
    }
}