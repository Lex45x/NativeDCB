namespace NativeDCB.Engine.Storage.EventLog;

public sealed record DatabaseOptions
{
    // Required for options binding.
    // ReSharper disable once UnusedMember.Global
    public DatabaseOptions()
    {
    }

    public DatabaseOptions(string directoryPath)
    {
        DirectoryPath = directoryPath;
    }

    // Public options can be populated with an object initializer.
    // ReSharper disable once AutoPropertyCanBeMadeGetOnly.Global
    public string DirectoryPath { get; init; } = string.Empty;

    public int MaxEventCountPerPartition { get; init; } = 10_000;

    // Retained as a public options alias.
    // ReSharper disable once UnusedMember.Global
    public int MaxEventsPerPartition
    {
        get => MaxEventCountPerPartition;
        init => MaxEventCountPerPartition = value;
    }

    internal string ValidateAndGetDirectory()
    {
        if (string.IsNullOrWhiteSpace(DirectoryPath))
        {
            throw new ArgumentException("A database directory is required.", nameof(DirectoryPath));
        }

        if (MaxEventCountPerPartition <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxEventCountPerPartition),
                "The partition event limit must be positive.");
        }

        return Path.GetFullPath(DirectoryPath);
    }
}