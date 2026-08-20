using System.Text.Json;

using NativeDCB.Model.Decisions;

namespace NativeDCB.Actors.Catalog;

public sealed record HandlerCatalogEntry(
    string Name,
    string CommandType,
    string NdlSource,
    string SourceFingerprint,
    string PlanFingerprint,
    string PlanJson,
    uint Version)
{
    public DecisionPlan ParsePlan()
    {
        return JsonSerializer.Deserialize<DecisionPlan>(PlanJson, CatalogJson.Options)
               ?? throw new InvalidDataException($"Stored handler plan '{Name}' is invalid.");
    }
}