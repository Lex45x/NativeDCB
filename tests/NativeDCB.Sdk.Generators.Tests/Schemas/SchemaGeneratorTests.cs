using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace NativeDCB.Sdk.Generators.Tests.Schemas;

public sealed class SchemaGeneratorTests
{
    [Fact]
    public void GeneratesDeterministicallyOrderedSchemaFactory()
    {
        const string source = """
                              using System;
                              namespace NativeDCB.Sdk
                              {
                                  [AttributeUsage(AttributeTargets.Class)]
                                  public sealed class EventTypeAttribute(string name) : Attribute { }
                                  [AttributeUsage(AttributeTargets.Class)]
                                  public sealed class CommandTypeAttribute(string name) : Attribute { }
                              }

                              [NativeDCB.Sdk.CommandType("z-command")]
                              public sealed record Command(string Id);
                              [NativeDCB.Sdk.EventType("a-event")]
                              public sealed record Event(string Id);
                              """;
        CSharpCompilation compilation = CreateCompilation(source);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new SchemaGenerator())
            .RunGenerators(compilation);

        GeneratorRunResult result = Assert.Single(driver.GetRunResult().Results);
        string generated = Assert.Single(result.GeneratedSources).SourceText.ToString();
        Assert.Contains("SchemaDescriptor.ForEvent<global::Event>()", generated, StringComparison.Ordinal);
        Assert.Contains("SchemaDescriptor.ForCommand<global::Command>()", generated, StringComparison.Ordinal);
        Assert.True(
            generated.IndexOf("ForEvent", StringComparison.Ordinal) <
            generated.IndexOf("ForCommand", StringComparison.Ordinal));
    }

    private static CSharpCompilation CreateCompilation(string source)
    {
        return CSharpCompilation.Create(
            "GeneratorTests",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview))],
            ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }
}
