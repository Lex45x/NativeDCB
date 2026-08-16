using System.Globalization;
using System.Text.Json;

using NativeDCB.Model;

namespace NativeDCB.Server.Storage;

internal sealed class RegisteredJsonSchema
{
    private RegisteredJsonSchema(
        string name,
        IReadOnlyDictionary<string, JsonPropertySchema> properties,
        bool additionalProperties)
    {
        Name = name;
        Properties = properties;
        AdditionalProperties = additionalProperties;
    }

    private string Name { get; }
    public IReadOnlyDictionary<string, JsonPropertySchema> Properties { get; }
    public bool AdditionalProperties { get; }

    private IEnumerable<JsonPropertySchema> ConsistencyKeys =>
        Properties.Values.Where(value => value.KeyName is not null);

    public static RegisteredJsonSchema Parse(string name, string document, bool eventSchema)
    {
        using JsonDocument parsed = JsonDocument.Parse(document);
        JsonElement root = parsed.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            (root.TryGetProperty("type", out JsonElement rootType) && rootType.GetString() != "object"))
        {
            throw new InvalidDataException("A schema document must describe a JSON object.");
        }

        HashSet<string> required = new(StringComparer.Ordinal);
        if (root.TryGetProperty("required", out JsonElement requiredElement))
        {
            if (requiredElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("Schema 'required' must be an array of property names.");
            }

            foreach (JsonElement value in requiredElement.EnumerateArray())
            {
                string property = value.ValueKind == JsonValueKind.String
                    ? value.GetString()!
                    : throw new InvalidDataException("Schema required-property names must be strings.");
                if (string.IsNullOrWhiteSpace(property) || !required.Add(property))
                {
                    throw new InvalidDataException("Schema required-property names must be non-empty and unique.");
                }
            }
        }

        if (!root.TryGetProperty("properties", out JsonElement propertiesElement) ||
            propertiesElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("A schema document must contain an object-valued 'properties' member.");
        }

        Dictionary<string, JsonPropertySchema> properties = new(StringComparer.Ordinal);
        foreach (JsonProperty property in propertiesElement.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(property.Name) || property.Value.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("Schema properties must have non-empty names and object definitions.");
            }

            if (!property.Value.TryGetProperty("type", out JsonElement typeElement) ||
                typeElement.ValueKind != JsonValueKind.String ||
                !JsonPropertySchema.SupportedTypes.Contains(typeElement.GetString()!))
            {
                throw new InvalidDataException(
                    $"Schema property '{property.Name}' has an unsupported or missing type.");
            }

            string? keyName = ReadKeyName(property.Value);
            if (keyName is not null &&
                typeElement.GetString() is not ("string" or "integer" or "number" or "boolean"))
            {
                throw new InvalidDataException(
                    $"Consistency-key property '{property.Name}' must have a scalar type.");
            }

            properties.Add(property.Name, new JsonPropertySchema(
                property.Name,
                typeElement.GetString()!,
                required.Contains(property.Name),
                keyName));
        }

        if (required.Except(properties.Keys).FirstOrDefault() is { } missing)
        {
            throw new InvalidDataException($"Required property '{missing}' is not declared in schema properties.");
        }

        JsonPropertySchema[] keys = properties.Values.Where(value => value.KeyName is not null).ToArray();
        if (eventSchema && keys.Length == 0)
        {
            throw new InvalidDataException("An event schema must declare at least one consistency key.");
        }

        if (keys.GroupBy(value => value.KeyName, StringComparer.Ordinal).Any(group => group.Count() > 1))
        {
            throw new InvalidDataException("Consistency-key names must be unique within an event schema.");
        }

        bool additionalProperties = !root.TryGetProperty("additionalProperties", out JsonElement additional) ||
                                    additional.ValueKind != JsonValueKind.False;
        return new RegisteredJsonSchema(name, properties, additionalProperties);
    }

    public void Validate(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException($"Value for schema '{Name}' must be a JSON object.");
        }

        foreach (JsonPropertySchema property in Properties.Values)
        {
            if (!value.TryGetProperty(property.Name, out JsonElement propertyValue))
            {
                if (property.Required)
                {
                    throw new ArgumentException(
                        $"Value for schema '{Name}' is missing required property '{property.Name}'.");
                }

                continue;
            }

            if (!property.Matches(propertyValue))
            {
                throw new ArgumentException(
                    $"Property '{Name}.{property.Name}' must have JSON type '{property.Type}'.");
            }
        }

        if (!AdditionalProperties)
        {
            foreach (JsonProperty extra in value.EnumerateObject().Where(item => !Properties.ContainsKey(item.Name)))
            {
                throw new ArgumentException($"Property '{Name}.{extra.Name}' is not declared by the schema.");
            }
        }
    }

    public EventKey[] ExtractKeys(JsonElement value)
    {
        Validate(value);
        return ConsistencyKeys.Select(property =>
        {
            if (!value.TryGetProperty(property.Name, out JsonElement keyValue) ||
                keyValue.ValueKind == JsonValueKind.Null)
            {
                throw new ArgumentException(
                    $"Consistency-key property '{Name}.{property.Name}' must have a non-null value.");
            }

            return new EventKey(property.KeyName!, Encode(keyValue));
        }).ToArray();
    }

    public string ResolveKeyName(string propertyName)
    {
        return Properties.TryGetValue(propertyName, out JsonPropertySchema? property) && property.KeyName is not null
            ? property.KeyName
            : throw new InvalidOperationException(
                $"Property '{Name}.{propertyName}' is not a registered consistency key.");
    }

    public IReadOnlyList<string> CompatibilityErrors(RegisteredJsonSchema previous)
    {
        List<string> errors = new();
        foreach (JsonPropertySchema oldProperty in previous.Properties.Values)
        {
            if (!Properties.TryGetValue(oldProperty.Name, out JsonPropertySchema? replacement))
            {
                if (oldProperty.Required || oldProperty.KeyName is not null)
                {
                    errors.Add($"Property '{oldProperty.Name}' cannot be removed.");
                }

                continue;
            }

            if (!string.Equals(oldProperty.Type, replacement.Type, StringComparison.Ordinal))
            {
                errors.Add($"Property '{oldProperty.Name}' cannot change type.");
            }

            if (!string.Equals(oldProperty.KeyName, replacement.KeyName, StringComparison.Ordinal))
            {
                errors.Add($"Property '{oldProperty.Name}' cannot change its consistency-key name.");
            }
        }

        foreach (JsonPropertySchema property in Properties.Values.Where(value => value.Required))
        {
            if (!previous.Properties.TryGetValue(property.Name, out JsonPropertySchema? old) || !old.Required)
            {
                errors.Add($"Property '{property.Name}' cannot become newly required.");
            }
        }

        return errors;
    }

    private static string? ReadKeyName(JsonElement property)
    {
        if (!property.TryGetProperty("x-native-dcb-consistency-key", out JsonElement key) &&
            !property.TryGetProperty("consistencyKey", out key))
        {
            return null;
        }

        if (key.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(key.GetString()))
        {
            throw new InvalidDataException("A consistency-key name must be a non-empty string.");
        }

        return key.GetString();
    }

    private static string Encode(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()!,
            JsonValueKind.Number when value.TryGetInt64(out long integer) => integer.ToString(CultureInfo
                .InvariantCulture),
            JsonValueKind.Number when value.TryGetUInt64(out ulong unsigned) => unsigned.ToString(CultureInfo
                .InvariantCulture),
            JsonValueKind.Number => value.GetDecimal().ToString(CultureInfo.InvariantCulture),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => throw new ArgumentException("Consistency-key values must be scalar JSON values.")
        };
    }
}

internal sealed record JsonPropertySchema(
    string Name,
    string Type,
    bool Required,
    string? KeyName)
{
    public static readonly HashSet<string> SupportedTypes =
        ["string", "integer", "number", "boolean", "object", "array", "null"];

    public bool Matches(JsonElement value)
    {
        return Type switch
        {
            "string" => value.ValueKind == JsonValueKind.String,
            "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
            "number" => value.ValueKind == JsonValueKind.Number,
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "object" => value.ValueKind == JsonValueKind.Object,
            "array" => value.ValueKind == JsonValueKind.Array,
            "null" => value.ValueKind == JsonValueKind.Null,
            _ => false
        };
    }
}