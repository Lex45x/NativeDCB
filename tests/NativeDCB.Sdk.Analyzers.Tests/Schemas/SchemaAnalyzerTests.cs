using System.Collections.Immutable;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace NativeDCB.Sdk.Analyzers.Tests.Schemas;

public sealed class SchemaAnalyzerTests
{
    [Fact]
    public async Task ReportsMissingAndDuplicateConsistencyKeys()
    {
        const string source = """
                              using System;
                              namespace NativeDCB.Sdk.Schemas
                              {
                                  [AttributeUsage(AttributeTargets.Class)]
                                  public sealed class EventTypeAttribute(string name) : Attribute { }
                                  [AttributeUsage(AttributeTargets.Property)]
                                  public sealed class ConsistencyKeyAttribute(string name) : Attribute { }
                              }

                              [NativeDCB.Sdk.Schemas.EventType("missing")]
                              public sealed record Missing(string Id);

                              [NativeDCB.Sdk.Schemas.EventType("duplicate")]
                              public sealed record Duplicate(
                                  [property: NativeDCB.Sdk.Schemas.ConsistencyKey("same")] string First,
                                  [property: NativeDCB.Sdk.Schemas.ConsistencyKey("same")] string Second);
                              """;
        CSharpCompilation compilation = CreateCompilation(source);
        ImmutableArray<Diagnostic> diagnostics = await compilation
            .WithAnalyzers([new SchemaAnalyzer()])
            .GetAnalyzerDiagnosticsAsync();

        Assert.Contains(diagnostics, value => value.Id == SchemaAnalyzer.MissingKeyId);
        Assert.Equal(expected: 2, diagnostics.Count(value => value.Id == SchemaAnalyzer.DuplicateKeyId));
    }

    [Fact]
    public async Task ReportsDuplicateEventNamesAcrossTypes()
    {
        const string source = """
                              using System;
                              namespace NativeDCB.Sdk.Schemas
                              {
                                  [AttributeUsage(AttributeTargets.Class)]
                                  public sealed class EventTypeAttribute(string name) : Attribute { }
                                  [AttributeUsage(AttributeTargets.Property)]
                                  public sealed class ConsistencyKeyAttribute(string name) : Attribute { }
                              }

                              [NativeDCB.Sdk.Schemas.EventType("same")]
                              public sealed record First([property: NativeDCB.Sdk.Schemas.ConsistencyKey("id")] string Id);
                              [NativeDCB.Sdk.Schemas.EventType("same")]
                              public sealed record Second([property: NativeDCB.Sdk.Schemas.ConsistencyKey("id")] string Id);
                              """;
        ImmutableArray<Diagnostic> diagnostics = await CreateCompilation(source)
            .WithAnalyzers([new SchemaAnalyzer()])
            .GetAnalyzerDiagnosticsAsync();

        Assert.Equal(expected: 2, diagnostics.Count(value => value.Id == SchemaAnalyzer.DuplicateEventId));
    }

    private static CSharpCompilation CreateCompilation(string source)
    {
        return CSharpCompilation.Create(
            "AnalyzerTests",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview))],
            ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }
}