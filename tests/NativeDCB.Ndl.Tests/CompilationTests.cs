using NativeDCB.Model;

namespace NativeDCB.Ndl.Tests;

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
}