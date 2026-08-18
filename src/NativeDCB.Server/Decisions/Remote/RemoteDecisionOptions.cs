namespace NativeDCB.Server.Decisions.Remote;

// These properties are populated by Microsoft.Extensions.Configuration at runtime.
// ReSharper disable AutoPropertyCanBeMadeGetOnly.Global
// ReSharper disable CollectionNeverUpdated.Global
// ReSharper disable UnusedAutoPropertyAccessor.Global
public sealed class RemoteDecisionOptions
{
    public string? ActiveKeyId { get; set; }
    public Dictionary<string, string> SigningKeys { get; set; } = new(StringComparer.Ordinal);
    public TimeSpan Lifetime { get; set; } = TimeSpan.FromMinutes(minutes: 5);
}