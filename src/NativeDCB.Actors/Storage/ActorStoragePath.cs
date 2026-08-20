using Microsoft.Extensions.Options;

namespace NativeDCB.Actors.Storage;

public sealed class ActorStoragePath(IOptions<ActorStorageOptions> options)
{
    private readonly string _root = Path.GetFullPath(options.Value.DatabaseRoot);

    public int MaxEventCountPerPartition { get; } = options.Value.MaxEventCountPerPartition > 0
        ? options.Value.MaxEventCountPerPartition
        : throw new ArgumentOutOfRangeException(
            nameof(options), "MaxEventCountPerPartition must be positive.");

    public string RootDirectory => _root;

    public void EnsureRootDirectory()
    {
        Directory.CreateDirectory(_root);
    }

    public string GetNewDatabaseDirectory(string database)
    {
        return ResolveDatabaseDirectory(database);
    }

    public string GetDatabaseDirectory(string database)
    {
        string path = ResolveDatabaseDirectory(database);

        if (!Directory.Exists(path) ||
            (!File.Exists(Path.Combine(path, "database_v1.json")) &&
             !Directory.EnumerateFiles(path, "store_partition_*_v1.json", SearchOption.TopDirectoryOnly).Any()))
        {
            throw new DirectoryNotFoundException($"Database '{database}' was not found.");
        }

        return path;
    }

    public IReadOnlyList<string> EnumerateDatabases()
    {
        EnsureRootDirectory();
        return Directory
            .EnumerateFiles(_root, "database_v1.json", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(
                _root, "store_partition_*_v1.json", SearchOption.AllDirectories))
            .Select(Path.GetDirectoryName)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(directory => Path.GetRelativePath(_root, directory)
                .Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    public string GetFile(string database, string fileName)
    {
        return Path.Combine(GetDatabaseDirectory(database), fileName);
    }

    public static string NormalizeDatabaseName(string database)
    {
        if (string.IsNullOrWhiteSpace(database) || Path.IsPathRooted(database))
        {
            throw new ArgumentException("A relative database name is required.", nameof(database));
        }

        string normalized = database.Replace('\\', '/').Trim('/');
        if (normalized.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new ArgumentException("The database name contains an invalid path segment.", nameof(database));
        }

        return normalized;
    }

    private string ResolveDatabaseDirectory(string database)
    {
        string normalized = NormalizeDatabaseName(database);
        string path = Path.GetFullPath(Path.Combine(
            _root, normalized.Replace('/', Path.DirectorySeparatorChar)));
        string prefix = _root.EndsWith(Path.DirectorySeparatorChar)
            ? _root
            : _root + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The database path must be below DatabaseRoot.", nameof(database));
        }

        return path;
    }
}