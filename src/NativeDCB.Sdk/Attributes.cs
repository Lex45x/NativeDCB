namespace NativeDCB.Sdk;

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

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public sealed class CommandTypeAttribute(string name) : Attribute
{
    public string Name { get; } = Validate(name);

    public string Identifier => Name;

    private static string Validate(string value)
    {
        return !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException("A command type name is required.", nameof(name));
    }
}

[AttributeUsage(AttributeTargets.Property)]
public sealed class ConsistencyKeyAttribute(string name) : Attribute
{
    public string Name { get; } = Validate(name);

    public string Tag => Name;

    private static string Validate(string value)
    {
        return !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException("A consistency key name is required.", nameof(name));
    }
}