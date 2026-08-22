using System.Runtime.InteropServices;

namespace NativeDCB.MicroBenchmarks.Infrastructure;

internal sealed class StorageFixture
{
    private readonly string _classRoot;
    private int _iteration;

    public StorageFixture(string benchmarkName)
    {
        string? configuredRoot = Environment.GetEnvironmentVariable("NATIVEDCB_BENCHMARK_ROOT");
        if (string.IsNullOrWhiteSpace(configuredRoot))
        {
            throw new InvalidOperationException(
                "Filesystem benchmarks require an explicit NATIVEDCB_BENCHMARK_ROOT on a known local volume.");
        }

        string root = Path.GetFullPath(configuredRoot);
        Directory.CreateDirectory(root);
        ConfiguredRoot = root;
        _classRoot = Path.Combine(root, "NativeDCB.MicroBenchmarks", benchmarkName + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_classRoot);
        TemplatePath = Path.Combine(_classRoot, "template");
        Directory.CreateDirectory(TemplatePath);
    }

    public string ConfiguredRoot { get; }
    public string TemplatePath { get; }
    public string? WorkingPath { get; private set; }

    public string CreateWorkingDirectory(bool copyTemplate = true)
    {
        DeleteWorkingDirectory();
        WorkingPath = Path.Combine(_classRoot, $"iteration-{Interlocked.Increment(ref _iteration):D8}");
        Directory.CreateDirectory(WorkingPath);
        if (copyTemplate)
        {
            CopyDirectory(TemplatePath, WorkingPath);
        }

        return WorkingPath;
    }

    public long TemplateBytes()
    {
        return Directory.EnumerateFiles(TemplatePath, "*", SearchOption.AllDirectories)
            .Sum(path => new FileInfo(path).Length);
    }

    public string DescribeEnvironment()
    {
        string driveRoot = Path.GetPathRoot(ConfiguredRoot)
            ?? throw new InvalidOperationException($"Storage root '{ConfiguredRoot}' has no drive root.");
        DriveInfo drive = new(driveRoot);
        return $"StorageRoot={ConfiguredRoot}; OS={RuntimeInformation.OSDescription}; " +
               $"Drive={drive.Name}; FileSystem={drive.DriveFormat}; FixtureBytes={TemplateBytes()}";
    }

    public void DeleteWorkingDirectory()
    {
        if (WorkingPath is not null && Directory.Exists(WorkingPath))
        {
            Directory.Delete(WorkingPath, recursive: true);
        }

        WorkingPath = null;
    }

    public void Dispose()
    {
        if (Directory.Exists(_classRoot))
        {
            Directory.Delete(_classRoot, recursive: true);
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)));
        }
    }
}
