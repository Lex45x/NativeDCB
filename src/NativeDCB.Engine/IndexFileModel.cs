using NativeDCB.Model;

namespace NativeDCB.Engine;

internal sealed record IndexFileModel(
    int FormatVersion,
    string EventType,
    EventKey Key,
    long Head,
    IReadOnlyList<SequencedEvent> Events,
    string EventSchemaFingerprint,
    string KeyEncodingFingerprint);