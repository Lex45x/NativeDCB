using NativeDCB.Model;
using NativeDCB.Model.Decisions;
using NativeDCB.Model.Decisions.Expressions;
using NativeDCB.Ndl.Compilation;
using NativeDCB.Ndl.Formatting;
using NativeDCB.Ndl.Tests.Support;

namespace NativeDCB.Ndl.Tests.Compilation;

public sealed class CompilationTests
{
    [Fact]
    public void CompilesNdlToAStorageNeutralDecisionPlan()
    {
        CompilationResult result = Ndl.Compile(TestSources.Complete);

        Assert.False(result.HasErrors);
        DecisionPlan plan = Assert.Single(result.Plans);
        Assert.Equal("SubscribeStudentToCourse", plan.Name);
        Assert.Equal("Contracts.SubscribeStudentToCourse", plan.CommandSchema);
        Assert.Equal("command", plan.CommandAlias);
        Assert.Equal(expected: 2, plan.Includes.Count);
        Assert.Equal(["CourseId"], plan.Includes[index: 0].KeyBindings.Select(value => value.PropertyName));
        Assert.Equal(["StudentId", "CourseId"],
            plan.Includes[index: 1].KeyBindings.Select(value => value.PropertyName));
        Assert.Equal(expected: 3, plan.Evaluation.Count);
        Assert.Equal(expected: 2, plan.Emissions.Count);
        Assert.Equal("ndl-v1", plan.Fingerprints.LanguageVersion);
        Assert.NotEmpty(plan.Fingerprints.SourceFingerprint);
    }

    [Fact]
    public void InvalidNdlDoesNotProduceAPlan()
    {
        CompilationResult result = Ndl.Compile("decision broken");

        Assert.True(result.HasErrors);
        Assert.Empty(result.Plans);
    }

    [Fact]
    public void FormatsACompiledPlanBackToCanonicalNdl()
    {
        DecisionPlan plan = Assert.Single(Ndl.Compile(TestSources.Complete).Plans);

        NdlPlanFormatResult result = Ndl.TryFormat(plan);

        Assert.True(result.Success);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(Ndl.Format(TestSources.Complete), result.NdlSource);
    }

    [Fact]
    public void RefusesToGenerateNdlWhenStoredBindingsWouldChange()
    {
        DecisionPlan plan = Assert.Single(Ndl.Compile(TestSources.Complete).Plans);
        PlanInclude first = plan.Includes[index: 0] with
        {
            KeyBindings = [new PlanKeyBinding("DifferentKey", new PlanSymbolExpression("command"))]
        };
        plan = plan with { Includes = [first, .. plan.Includes.Skip(count: 1)] };

        NdlPlanFormatResult result = Ndl.TryFormat(plan);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, value => value.Path.Contains("KeyBindings", StringComparison.Ordinal));
    }

    [Fact]
    public void RefusesToGenerateNdlForNamesOutsideTheLanguageGrammar()
    {
        DecisionPlan plan = Assert.Single(Ndl.Compile(TestSources.Complete).Plans);
        plan = plan with { CommandSchema = "subscribe-command" };

        NdlPlanFormatResult result = Ndl.TryFormat(plan);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, value => value.Path == "CommandSchema");
        NdlPlanFormatException exception = Assert.Throws<NdlPlanFormatException>(() => Ndl.Format(plan));
        Assert.NotEmpty(exception.Diagnostics);
    }
}