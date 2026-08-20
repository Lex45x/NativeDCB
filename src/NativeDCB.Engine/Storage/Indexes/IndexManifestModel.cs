using NativeDCB.Model;
using NativeDCB.Model.Events;

namespace NativeDCB.Engine.Storage.Indexes;

internal sealed record IndexManifestModel(
    int FormatVersion,
    string EventType,
    EventKey Key,
    long Head,
    string GenerationFile,
    string EventSchemaFingerprint,
    string KeyEncodingFingerprint);