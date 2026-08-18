using System.Collections.Concurrent;

namespace NativeDCB.Server.Catalog;

public sealed class CatalogDocument
{
    public int FormatVersion { get; init; } = 1;
    public ConcurrentDictionary<string, SchemaCatalogEntry> EventSchemas { get; init; } = new(StringComparer.Ordinal);
    public ConcurrentDictionary<string, SchemaCatalogEntry> CommandSchemas { get; init; } = new(StringComparer.Ordinal);
    public ConcurrentDictionary<string, HandlerCatalogEntry> Handlers { get; init; } = new(StringComparer.Ordinal);
}