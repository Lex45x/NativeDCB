namespace NativeDCB.Sdk;

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