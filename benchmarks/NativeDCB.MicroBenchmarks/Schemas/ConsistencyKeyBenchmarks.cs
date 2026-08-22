using BenchmarkDotNet.Attributes;

using NativeDCB.Sdk.Schemas;

namespace NativeDCB.MicroBenchmarks.Schemas;

[MemoryDiagnoser]
public class ConsistencyKeyBenchmarks
{
    public IEnumerable<KeyCase> ExtractionCases()
    {
        foreach (int keyCount in new[] { 1, 4 })
        {
            foreach (int stringLength in new[] { 8, 64 })
            {
                string text = new('k', stringLength);
                yield return keyCount == 1
                    ? Case($"Keys=1,Type=String,StringBytes={stringLength}", new StringKey1(text))
                    : Case($"Keys=4,Type=String,StringBytes={stringLength}",
                        new StringKey4(text, text, text, text));
            }

            yield return keyCount == 1
                ? Case("Keys=1,Type=Integer", new IntegerKey1(104729))
                : Case("Keys=4,Type=Integer", new IntegerKey4(1, 4, 16, 64));
            yield return keyCount == 1
                ? Case("Keys=1,Type=Guid", new GuidKey1(Guid.Parse("01234567-89ab-cdef-0123-456789abcdef")))
                : Case("Keys=4,Type=Guid", new GuidKey4(
                    Guid.Parse("01234567-89ab-cdef-0123-456789abcdef"),
                    Guid.Parse("11234567-89ab-cdef-0123-456789abcdef"),
                    Guid.Parse("21234567-89ab-cdef-0123-456789abcdef"),
                    Guid.Parse("31234567-89ab-cdef-0123-456789abcdef")));
        }
    }

    public IEnumerable<KeyCase> EncodingCases() => ExtractionCases()
        .Where(input => input.Schema.ConsistencyKeys.Count == 1);

    [Benchmark]
    [ArgumentsSource(nameof(ExtractionCases))]
    public object ExtractTags(KeyCase input) => input.Schema.ExtractTags(input.Instance);

    [Benchmark]
    [ArgumentsSource(nameof(EncodingCases))]
    public string EncodeKey(KeyCase input) => input.Schema.ConsistencyKeys[0].Encode(input.Instance);

    private static KeyCase Case<T>(string shape, T instance) where T : notnull =>
        new(shape, SchemaDescriptor.ForEvent<T>(), instance);

    public sealed record KeyCase(string Shape, SchemaDescriptor Schema, object Instance)
    {
        public override string ToString() => Shape;
    }

    [EventType("string-key-1")]
    public sealed record StringKey1([property: ConsistencyKey("k1")] string K1);

    [EventType("string-key-4")]
    public sealed record StringKey4(
        [property: ConsistencyKey("k1")] string K1,
        [property: ConsistencyKey("k2")] string K2,
        [property: ConsistencyKey("k3")] string K3,
        [property: ConsistencyKey("k4")] string K4);

    [EventType("integer-key-1")]
    public sealed record IntegerKey1([property: ConsistencyKey("k1")] long K1);

    [EventType("integer-key-4")]
    public sealed record IntegerKey4(
        [property: ConsistencyKey("k1")] long K1,
        [property: ConsistencyKey("k2")] long K2,
        [property: ConsistencyKey("k3")] long K3,
        [property: ConsistencyKey("k4")] long K4);

    [EventType("guid-key-1")]
    public sealed record GuidKey1([property: ConsistencyKey("k1")] Guid K1);

    [EventType("guid-key-4")]
    public sealed record GuidKey4(
        [property: ConsistencyKey("k1")] Guid K1,
        [property: ConsistencyKey("k2")] Guid K2,
        [property: ConsistencyKey("k3")] Guid K3,
        [property: ConsistencyKey("k4")] Guid K4);
}
