using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;

namespace NativeDCB.MicroBenchmarks.Infrastructure;

internal sealed class EnvironmentColumn(string name, string value, string legend) : IColumn
{
    public string Id => $"NativeDCB.{name}";
    public string ColumnName => name;
    public string Legend => legend;
    public bool AlwaysShow => true;
    public ColumnCategory Category => ColumnCategory.Custom;
    public int PriorityInCategory => 0;
    public bool IsNumeric => false;
    public UnitType UnitType => UnitType.Dimensionless;

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase) => value;

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase, SummaryStyle style) => value;

    public bool IsDefault(Summary summary, BenchmarkCase benchmarkCase) => false;

    public bool IsAvailable(Summary summary) => true;
}
