using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

using NativeDCB.Model;

namespace NativeDCB.Sdk;

public sealed record SchemaDescriptor(
    Type ClrType,
    string Name,
    SchemaType SchemaType,
    IReadOnlyList<ConsistencyKeyDescriptor> ConsistencyKeys)
{
    public static SchemaDescriptor ForEvent<TEvent>()
    {
        return ForEvent(typeof(TEvent));
    }

    public static SchemaDescriptor ForEvent(Type eventType)
    {
        ArgumentNullException.ThrowIfNull(eventType);
        EventTypeAttribute? attribute = eventType.GetCustomAttribute<EventTypeAttribute>();
        if (attribute is null)
        {
            throw new InvalidOperationException($"Event type '{eventType.FullName}' must have EventTypeAttribute.");
        }

        IReadOnlyList<ConsistencyKeyDescriptor> keys = GetKeys(eventType);
        if (keys.Count == 0)
        {
            throw new InvalidOperationException(
                $"Event type '{eventType.FullName}' must declare at least one consistency key.");
        }

        return new SchemaDescriptor(eventType, attribute.Name, SchemaType.Event, keys);
    }

    public static SchemaDescriptor ForCommand<TCommand>()
    {
        return ForCommand(typeof(TCommand));
    }

    public static SchemaDescriptor ForCommand(Type commandType)
    {
        ArgumentNullException.ThrowIfNull(commandType);
        CommandTypeAttribute? attribute = commandType.GetCustomAttribute<CommandTypeAttribute>();
        if (attribute is null)
        {
            throw new InvalidOperationException(
                $"Command type '{commandType.FullName}' must have CommandTypeAttribute.");
        }

        return new SchemaDescriptor(commandType, attribute.Name, SchemaType.Command, GetKeys(commandType));
    }

    public IReadOnlyList<EventKey> ExtractTags(object instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (!ClrType.IsInstanceOfType(instance))
        {
            throw new ArgumentException($"Expected an instance of '{ClrType.FullName}'.", nameof(instance));
        }

        return ConsistencyKeys
            .Select(key => new EventKey(key.Name, key.Encode(instance)))
            .ToArray();
    }

    public string ToJsonSchemaDocument()
    {
        JsonObject properties = new();
        JsonArray required = new();
        NullabilityInfoContext nullability = new();
        foreach (PropertyInfo property in ClrType.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                     .Where(value => value.GetMethod is { IsStatic: false } && value.GetIndexParameters().Length == 0))
        {
            Type propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            JsonObject definition = new() { ["type"] = JsonType(propertyType) };
            ConsistencyKeyDescriptor? key = ConsistencyKeys.SingleOrDefault(value => value.Property == property);
            if (key is not null)
            {
                definition["x-native-dcb-consistency-key"] = key.Name;
            }

            properties[property.Name] = definition;
            NullabilityInfo nullabilityInfo = nullability.Create(property);
            if (Nullable.GetUnderlyingType(property.PropertyType) is null &&
                nullabilityInfo.ReadState != NullabilityState.Nullable)
            {
                required.Add(property.Name);
            }
        }

        JsonObject schema = new()
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required,
            ["additionalProperties"] = false
        };
        return schema.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    internal static string EncodeValue(object? value)
    {
        return value switch
        {
            null => throw new InvalidOperationException("Consistency key values cannot be null."),
            string text => text,
            Guid guid => guid.ToString("D"),
            DateTime { Kind: DateTimeKind.Unspecified } => throw new InvalidOperationException(
                "An unspecified DateTime cannot be encoded as a deterministic consistency key."),
            DateTime dateTime => dateTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToUniversalTime()
                .ToString("O", CultureInfo.InvariantCulture),
            bool boolean => boolean ? "true" : "false",
            Enum enumeration => enumeration.ToString(),
            byte number => number.ToString(CultureInfo.InvariantCulture),
            sbyte number => number.ToString(CultureInfo.InvariantCulture),
            short number => number.ToString(CultureInfo.InvariantCulture),
            ushort number => number.ToString(CultureInfo.InvariantCulture),
            int number => number.ToString(CultureInfo.InvariantCulture),
            uint number => number.ToString(CultureInfo.InvariantCulture),
            long number => number.ToString(CultureInfo.InvariantCulture),
            ulong number => number.ToString(CultureInfo.InvariantCulture),
            decimal number => number.ToString(CultureInfo.InvariantCulture),
            _ => throw new InvalidOperationException(
                $"Consistency key type '{value.GetType().FullName}' does not have a deterministic encoder.")
        };
    }

    private static IReadOnlyList<ConsistencyKeyDescriptor> GetKeys(Type type)
    {
        ConsistencyKeyDescriptor[] keys = type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => (Property: property, Attribute: property.GetCustomAttribute<ConsistencyKeyAttribute>()))
            .Where(item => item.Attribute is not null)
            .Select(item =>
            {
                if (item.Property.GetMethod is null || item.Property.GetMethod.IsStatic ||
                    item.Property.GetIndexParameters().Length != 0)
                {
                    throw new InvalidOperationException(
                        $"Consistency key '{type.FullName}.{item.Property.Name}' must be a readable direct instance property.");
                }

                return new ConsistencyKeyDescriptor(item.Attribute!.Name, item.Property);
            })
            .ToArray();

        IGrouping<string, ConsistencyKeyDescriptor>? duplicate = keys.GroupBy(key => key.Name, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"Consistency key name '{duplicate.Key}' is duplicated on '{type.FullName}'.");
        }

        return keys;
    }

    private static string JsonType(Type type)
    {
        if (type == typeof(string) || type == typeof(char) || type == typeof(Guid) ||
            type == typeof(DateTime) || type == typeof(DateTimeOffset) || type.IsEnum)
        {
            return "string";
        }

        if (type == typeof(bool))
        {
            return "boolean";
        }

        if (type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) ||
            type == typeof(ushort) || type == typeof(int) || type == typeof(uint) ||
            type == typeof(long) || type == typeof(ulong))
        {
            return "integer";
        }

        if (type == typeof(float) || type == typeof(double) || type == typeof(decimal))
        {
            return "number";
        }

        if (type != typeof(string) && typeof(IEnumerable).IsAssignableFrom(type))
        {
            return "array";
        }

        return "object";
    }
}