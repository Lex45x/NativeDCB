using System.Text.Json;

namespace NativeDCB.Server.Storage;

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