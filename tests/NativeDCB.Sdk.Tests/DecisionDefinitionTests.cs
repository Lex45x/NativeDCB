using System.Linq.Expressions;

using NativeDCB.Model;

namespace NativeDCB.Sdk.Tests;

public sealed class DecisionDefinitionTests
{
    public static TheoryData<Func<DecisionDefinition<Subscribe>>, string> InvalidDefinitions => new()
    {
        { () => BuildInvalid(@event => @event.Capacity > 0), "NDCB101" },
        { () => BuildInvalid(@event => @event.Capacity == 5), "NDCB104" },
        // ReSharper disable once EqualExpressionComparison
        { () => BuildInvalid(@event => @event.CourseId == @event.CourseId), "NDCB105" }
    };

    [Fact]
    public void Fluent_definition_preserves_include_and_query_order()
    {
        Subscribe command = new("student-1", "course-1");

        DecisionDefinition<Subscribe> definition = Decision.WithDecisionModel(command)
            .Include<CourseDefined, CourseModel>((_, @event) => new CourseModel(true, @event.Capacity, 0))
            .Where(@event => @event.CourseId == command.CourseId)
            .Include<Subscribed>((model, _) => new CourseModel(model.Exists, model.Capacity, model.Count + 1))
            .Where(@event => @event.CourseId == command.CourseId && @event.StudentId == command.StudentId)
            .Evaluate((model, input) => model.Exists && model.Count < model.Capacity && input.CourseId.Length > 0)
            .Decide((accepted, input) => accepted ? input.StudentId : "rejected");

        Assert.True(definition.IsValid);
        Assert.Same(command, definition.Command);
        Assert.Collection(
            definition.Includes,
            include => Assert.Equal(typeof(CourseDefined), include.EventType),
            include => Assert.Equal(typeof(Subscribed), include.EventType));
        Assert.Collection(
            definition.QueryTemplates,
            item =>
            {
                Assert.Equal(["course-defined"], item.EventTypes);
                Assert.Equal(("course", "course-1"), (item.Keys[index: 0].Name, item.Keys[index: 0].Value));
            },
            item =>
            {
                Assert.Equal(["subscribed"], item.EventTypes);
                Assert.Equal(["course", "student"], item.Keys.Select(key => key.Name));
                Assert.Equal(["course-1", "student-1"], item.Keys.Select(key => key.Value));
            });
        Assert.All(definition.Includes, include => Assert.IsAssignableFrom<LambdaExpression>(include.Reducer));
        Assert.IsAssignableFrom<LambdaExpression>(definition.EvaluationExpression);
        Assert.IsAssignableFrom<LambdaExpression>(definition.DecisionExpression);
    }

    [Fact]
    public void Fluent_runtime_types_enforce_method_order()
    {
        DecisionModelBuilder<Subscribe, EmptyDecisionModel> start = Decision.WithDecisionModel(new Subscribe("s", "c"));
        DecisionModelEventSpec<Subscribe, EmptyDecisionModel, CourseDefined, CourseModel> eventSpec =
            start.Include<CourseDefined, CourseModel>((_, @event) => new CourseModel(true, @event.Capacity, 0));
        DecisionModelBuilder<Subscribe, CourseModel> completed = eventSpec.Where(@event => @event.CourseId == "c");
        EvaluatedDecision<Subscribe, CourseModel, bool> evaluated =
            completed.Evaluate((model, command) => model.Exists && command.CourseId == "c");

        Assert.Contains(start.GetType().GetMethods(), method => method.Name == "Include");
        Assert.DoesNotContain(start.GetType().GetMethods(), method => method.Name is "Where" or "Decide");
        Assert.Contains(eventSpec.GetType().GetMethods(), method => method.Name == "Where");
        Assert.DoesNotContain(eventSpec.GetType().GetMethods(),
            method => method.Name is "Include" or "Evaluate" or "Decide");
        Assert.Contains(completed.GetType().GetMethods(), method => method.Name is "Include");
        Assert.Contains(completed.GetType().GetMethods(), method => method.Name is "Evaluate");
        Assert.DoesNotContain(completed.GetType().GetMethods(), method => method.Name is "Where" or "Decide");
        Assert.Contains(evaluated.GetType().GetMethods(), method => method.Name == "Decide");
        Assert.DoesNotContain(evaluated.GetType().GetMethods(),
            method => method.Name is "Include" or "Where" or "Evaluate");
    }

    [Fact]
    public void Evaluate_captures_typed_command_parameter_and_remains_executable()
    {
        Subscribe command = new("student-7", "course-9");
        DecisionDefinition<Subscribe> definition = Decision.WithDecisionModel(command)
            .Include<CourseDefined, CourseModel>((_, @event) => new CourseModel(true, @event.Capacity, 0))
            .Where(@event => @event.CourseId == command.CourseId)
            .Evaluate((model, input) => $"{input.StudentId}:{model.Capacity}")
            .Decide((evaluation, input) => evaluation + ":" + input.CourseId);

        Expression<Func<CourseModel, Subscribe, string>> evaluation =
            Assert.IsAssignableFrom<Expression<Func<CourseModel, Subscribe, string>>>(
                definition.EvaluationExpression);

        Assert.Equal(typeof(Subscribe), evaluation.Parameters[index: 1].Type);
        Assert.Equal("student-7:12",
            evaluation.Compile()(new CourseModel(Exists: true, Capacity: 12, Count: 0), command));
    }

    [Fact]
    public void Where_translates_reversed_equality_operand()
    {
        Subscribe command = new("student", "course");

        DecisionDefinition<Subscribe> definition = Decision.WithDecisionModel(command)
            .Include<CourseDefined, CourseModel>((_, @event) => new CourseModel(true, @event.Capacity, 0))
            .Where(@event => command.CourseId == @event.CourseId)
            .Evaluate((model, _) => model.Exists)
            .Decide((accepted, _) => accepted);

        Assert.True(definition.IsValid);
        Assert.Equal("course", definition.QueryTemplates[index: 0].Keys[index: 0].Value);
    }

    [Fact]
    public void Fluent_definition_compiles_to_the_shared_decision_plan()
    {
        Subscribe command = new("student-1", "course-1");
        DecisionDefinition<Subscribe> definition = Decision.WithDecisionModel(command)
            .Include<CourseDefined, CourseModel>((_, @event) => new CourseModel(true, @event.Capacity, 0))
            .Where(@event => @event.CourseId == command.CourseId)
            .Evaluate((model, _) => model.Exists && model.Count < model.Capacity)
            .Decide((accepted, input) => accepted
                ? Decision.Accept(new Subscribed(input.StudentId, input.CourseId))
                : Decision.Reject("Course is unavailable"));

        DecisionPlan plan = definition.Compile("SubscribeStudent");

        Assert.Equal("sdk-v1", plan.Fingerprints.LanguageVersion);
        Assert.Equal("subscribe", plan.CommandSchema);
        PlanInclude include = Assert.Single(plan.Includes);
        Assert.Equal("course-defined", include.EventType);
        Assert.Equal("CourseId", Assert.Single(include.KeyBindings).PropertyName);
        PlanRequirement requirement = Assert.IsType<PlanRequirement>(plan.Evaluation[index: 1]);
        Assert.Equal("Course is unavailable", Assert.IsType<PlanLiteralExpression>(requirement.Reason).Value);
        Assert.Equal("subscribed", Assert.Single(plan.Emissions).EventType);
        Assert.Contains("course-defined", plan.Fingerprints.SchemaFingerprints.Keys);
        Assert.Contains("subscribed", plan.Fingerprints.SchemaFingerprints.Keys);
    }

    [Theory]
    [MemberData(nameof(InvalidDefinitions))]
    public void Invalid_where_predicates_expose_diagnostics(
        Func<DecisionDefinition<Subscribe>> define,
        string expectedCode)
    {
        DecisionDefinition<Subscribe> definition = define();

        Assert.False(definition.IsValid);
        Assert.Empty(definition.QueryTemplates);
        Assert.Contains(definition.Diagnostics, diagnostic =>
            diagnostic.Code == expectedCode && diagnostic.Severity == SdkDiagnosticSeverity.Error);
    }

    private static DecisionDefinition<Subscribe> BuildInvalid(Expression<Func<CourseDefined, bool>> predicate)
    {
        return Decision.WithDecisionModel(new Subscribe("student", "course"))
            .Include<CourseDefined, CourseModel>((_, @event) => new CourseModel(true, @event.Capacity, 0))
            .Where(predicate)
            .Evaluate((model, _) => model.Exists)
            .Decide((accepted, _) => accepted);
    }

    [CommandType("subscribe")]
    public sealed record Subscribe(string StudentId, string CourseId);

    [EventType("course-defined")]
    // ReSharper disable once MemberCanBePrivate.Global
    public sealed record CourseDefined(
        [property: ConsistencyKey("course")] string CourseId,
        int Capacity);

    [EventType("subscribed")]
    // ReSharper disable once MemberCanBePrivate.Global
    public sealed record Subscribed(
        [property: ConsistencyKey("student")] string StudentId,
        [property: ConsistencyKey("course")] string CourseId);

    // ReSharper disable once MemberCanBePrivate.Global
    public sealed record CourseModel(bool Exists, int Capacity, int Count);
}