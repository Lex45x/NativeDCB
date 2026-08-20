namespace NativeDCB.Actors.Catalog;

public sealed record SchemaCatalogEntry(
    string Name,
    string DocumentJson,
    string Fingerprint,
    uint Version);