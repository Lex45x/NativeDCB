namespace NativeDCB.Sdk.Schemas;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public sealed class EventTypeAttribute(string name) : Attribute
{
    public string Name { get; } = Validate(name);

    public string Identifier => Name;

    private static string Validate(string value)
    {
        return !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException("An event type name is required.", nameof(name));
    }
}