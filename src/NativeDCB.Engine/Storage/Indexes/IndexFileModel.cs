using NativeDCB.Model;
using NativeDCB.Model.Events;

namespace NativeDCB.Engine.Storage.Indexes;

internal sealed record IndexFileModel(
    int FormatVersion,
    string EventType,
    EventKey Key,
    long Head,
    IReadOnlyList<SequencedEvent> Events,
    string EventSchemaFingerprint,
    string KeyEncodingFingerprint);