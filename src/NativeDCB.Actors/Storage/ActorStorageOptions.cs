namespace NativeDCB.Actors.Storage;

public sealed class ActorStorageOptions
{
    public string DatabaseRoot { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NativeDCB",
        "databases");

    public int MaxEventCountPerPartition { get; set; } = 10_000;
}