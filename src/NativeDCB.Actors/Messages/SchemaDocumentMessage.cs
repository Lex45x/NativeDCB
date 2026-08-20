// Orleans serialization contracts are consumed across process boundaries.
// ReSharper disable NotAccessedPositionalProperty.Global

namespace NativeDCB.Actors.Messages;

[GenerateSerializer]
[Immutable]
public sealed record SchemaDocumentMessage(
    [property: Id(id: 0)] string Name,
    [property: Id(id: 1)] string DocumentJson,
    [property: Id(id: 2)] string Fingerprint);