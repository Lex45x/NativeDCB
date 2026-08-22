using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using BenchmarkDotNet.Attributes;

using NativeDCB.Actors.Catalog.Schemas;

namespace NativeDCB.MicroBenchmarks.Schemas;

[MemoryDiagnoser]
public class SchemaBenchmarks
{
    public IEnumerable<SchemaParseCase> ParseCases()
    {
        foreach (int properties in new[] { 4, 16, 64 })
        foreach (double requiredRatio in new[] { 0.25, 0.5, 1.0 })
        foreach (int keys in new[] { 1, 4 })
        {
            string document = BuildSchema(properties, requiredRatio, Math.Min(keys, properties));
            yield return new SchemaParseCase(
                $"Properties={properties},Required={requiredRatio:P0},Keys={Math.Min(keys, properties)},SchemaBytes={Encoding.UTF8.GetByteCount(document)}",
                document);
        }
    }

    public IEnumerable<object[]> PayloadCases()
    {
        foreach (int properties in new[] { 4, 64 })
        foreach (double requiredRatio in new[] { 0.25, 1.0 })
        foreach (int keys in new[] { 1, 4 })
        foreach (int requestedPayloadBytes in new[] { 2048, 16_384 })
        foreach (bool valid in new[] { true, false })
        {
            int keyCount = Math.Min(keys, properties);
            string document = BuildSchema(properties, requiredRatio, keyCount);
            JsonElement payload = BuildPayload(
                properties, requiredRatio, keyCount, requestedPayloadBytes, valid);
            int actualPayloadBytes = Encoding.UTF8.GetByteCount(payload.GetRawText());
            yield return
            [
                new SchemaPayloadCase(
                    $"Properties={properties},Required={requiredRatio:P0},Keys={keyCount},Valid={valid}",
                    RegisteredJsonSchema.Parse("benchmark-event", document, eventSchema: true),
                    payload),
                actualPayloadBytes
            ];
        }
    }

    [Benchmark]
    [ArgumentsSource(nameof(ParseCases))]
    public RegisteredJsonSchema Parse(SchemaParseCase input) => RegisteredJsonSchema.Parse(
        "benchmark-event", input.Document, eventSchema: true);

    [Benchmark]
    [ArgumentsSource(nameof(PayloadCases))]
    public bool ValidatePayload(SchemaPayloadCase input, int payloadBytes)
    {
        _ = payloadBytes;
        try
        {
            input.Schema.Validate(input.Payload);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    [Benchmark]
    [ArgumentsSource(nameof(PayloadCases))]
    public object ExtractKeys(SchemaPayloadCase input, int payloadBytes)
    {
        _ = payloadBytes;
        try
        {
            return input.Schema.ExtractKeys(input.Payload);
        }
        catch (ArgumentException)
        {
            return Array.Empty<object>();
        }
    }

    private static string BuildSchema(int properties, double requiredRatio, int keys)
    {
        JsonObject definitions = new();
        JsonArray required = new();
        int requiredCount = (int)Math.Round(properties * requiredRatio, MidpointRounding.AwayFromZero);
        for (int index = 0; index < properties; index++)
        {
            string name = $"property{index:D3}";
            JsonObject definition = new() { ["type"] = "string" };
            if (index < keys)
            {
                definition["x-native-dcb-consistency-key"] = $"key{index}";
            }

            definitions[name] = definition;
            if (index < requiredCount)
            {
                required.Add(name);
            }
        }

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = definitions,
            ["required"] = required,
            ["additionalProperties"] = false
        }.ToJsonString();
    }

    private static JsonElement BuildPayload(
        int properties,
        double requiredRatio,
        int keys,
        int requestedBytes,
        bool valid)
    {
        JsonObject payload = new();
        int requiredCount = (int)Math.Round(properties * requiredRatio, MidpointRounding.AwayFromZero);
        HashSet<int> included = Enumerable.Range(0, requiredCount)
            .Concat(Enumerable.Range(0, keys))
            .Append(properties - 1)
            .ToHashSet();
        if (!valid)
        {
            included.Remove(0);
        }

        foreach (int index in included.Order())
        {
            payload[$"property{index:D3}"] = string.Empty;
        }

        int baseBytes = Encoding.UTF8.GetByteCount(payload.ToJsonString());
        int actualBytes = Math.Max(requestedBytes, baseBytes);
        string paddingProperty = $"property{properties - 1:D3}";
        payload[paddingProperty] = new string('x', actualBytes - baseBytes);
        JsonElement result = JsonSerializer.SerializeToElement(payload);
        if (Encoding.UTF8.GetByteCount(result.GetRawText()) != actualBytes)
        {
            throw new InvalidOperationException("Schema payload byte cardinality was not deterministic.");
        }

        return result;
    }

    public sealed record SchemaParseCase(string Shape, string Document)
    {
        public override string ToString() => Shape;
    }

    public sealed record SchemaPayloadCase(string Shape, RegisteredJsonSchema Schema, JsonElement Payload)
    {
        public override string ToString() => Shape;
    }
}
