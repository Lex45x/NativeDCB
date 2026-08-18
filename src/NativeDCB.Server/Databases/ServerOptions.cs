namespace NativeDCB.Server.Databases;

public sealed class ServerOptions
{
    // ReSharper disable once AutoPropertyCanBeMadeGetOnly.Global -- Set by the configuration binder.
    public string DatabaseRoot { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NativeDCB",
        "databases");

    // ReSharper disable once AutoPropertyCanBeMadeGetOnly.Global -- Set by the configuration binder.
    public int MaxEventCountPerPartition { get; set; } = 10_000;
}