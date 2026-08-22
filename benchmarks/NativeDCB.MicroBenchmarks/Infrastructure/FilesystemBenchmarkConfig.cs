using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Exporters.Csv;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Loggers;
using System.Runtime.InteropServices;

namespace NativeDCB.MicroBenchmarks.Infrastructure;

public sealed class FilesystemBenchmarkConfig : ManualConfig
{
    public FilesystemBenchmarkConfig()
    {
        AddJob(Job.Default
            .WithId("Filesystem")
            .WithInvocationCount(1)
            .WithUnrollFactor(1));
        AddLogger(ConsoleLogger.Default);
        AddColumnProvider(DefaultColumnProviders.Instance);
        AddColumn(
            new EnvironmentColumn("StorageRoot", StorageRoot(), "Resolved NATIVEDCB_BENCHMARK_ROOT."),
            new EnvironmentColumn("OS", RuntimeInformation.OSDescription, "Operating system description."),
            new EnvironmentColumn("Drive", DriveMetadata().Name, "Storage volume root."),
            new EnvironmentColumn("FileSystem", DriveMetadata().Format, "Storage volume filesystem."));
        AddExporter(MarkdownExporter.GitHub, CsvExporter.Default);
    }

    private static string StorageRoot()
    {
        string? root = Environment.GetEnvironmentVariable("NATIVEDCB_BENCHMARK_ROOT");
        return string.IsNullOrWhiteSpace(root) ? "<required>" : Path.GetFullPath(root);
    }

    private static (string Name, string Format) DriveMetadata()
    {
        try
        {
            string root = StorageRoot();
            if (root == "<required>")
            {
                return ("<unknown>", "<unknown>");
            }

            DriveInfo drive = new(Path.GetPathRoot(root)!);
            return (drive.Name, drive.DriveFormat);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return ("<unknown>", "<unknown>");
        }
    }
}
