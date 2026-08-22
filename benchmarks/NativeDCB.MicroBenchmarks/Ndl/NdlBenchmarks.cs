using System.Text;

using BenchmarkDotNet.Attributes;

using NativeDCB.Ndl;
using NativeDCB.Ndl.Formatting;

namespace NativeDCB.MicroBenchmarks.Ndl;

[MemoryDiagnoser]
public class NdlBenchmarks
{
    private string _source = null!;

    [Params(1, 16)]
    public int DecisionCount { get; set; }

    [Params(8192, 32_768)]
    public int SourceBytes { get; set; }

    [Params(true, false)]
    public bool Valid { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        StringBuilder source = new();
        for (int index = 0; index < DecisionCount; index++)
        {
            source.AppendLine($$"""
                decision Benchmark{{index}}
                from BenchmarkCommand command
                | include BenchmarkEvent event
                    where event.Id == command.Id
                    apply { Count = previous.Count ?? 0 + 1 }
                | evaluate {
                    require model.Count >= 0 else "invalid";
                }
                | decide {
                    emit BenchmarkAccepted { Id = command.Id, Count = model.Count };
                };

                """);
        }

        if (!Valid)
        {
            source.Append("decision broken from");
        }

        int currentBytes = Encoding.UTF8.GetByteCount(source.ToString());
        if (currentBytes > SourceBytes)
        {
            throw new InvalidOperationException(
                $"The generated {DecisionCount}-decision source exceeds the {SourceBytes}-byte shape.");
        }

        source.Append(' ', SourceBytes - currentBytes);
        _source = source.ToString();
        if (Encoding.UTF8.GetByteCount(_source) != SourceBytes)
        {
            throw new InvalidOperationException("NDL source byte cardinality was not deterministic.");
        }
    }

    [Benchmark]
    public object Lex() => global::NativeDCB.Ndl.Ndl.Lex(_source);

    [Benchmark]
    public object Parse() => global::NativeDCB.Ndl.Ndl.Parse(_source);

    [Benchmark]
    public object Compile() => global::NativeDCB.Ndl.Ndl.Compile(_source);

    [Benchmark]
    public string Format()
    {
        try
        {
            return global::NativeDCB.Ndl.Ndl.Format(_source);
        }
        catch (NdlFormatException)
        {
            return string.Empty;
        }
    }
}
