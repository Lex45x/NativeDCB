using System.Text.Json;

using NativeDCB.Model;

namespace NativeDCB.Server.Storage;

public sealed record HandlerCatalogEntry(
    string Name,
    string CommandType,
    string NdlSource,
    string SourceFingerprint,
    string PlanFingerprint,
    string PlanJson)
{
    public DecisionPlan ParsePlan()
    {
        return JsonSerializer.Deserialize<DecisionPlan>(
                   PlanJson, DatabaseRegistry.JsonOptions)
               ?? throw new InvalidDataException($"Stored handler plan '{Name}' is invalid.");
    }
}