using NativeDCB.Engine;
using NativeDCB.Engine.Actors;
using NativeDCB.Model;

namespace NativeDCB.Server.Storage;

public sealed class CatalogEventBatchValidator(DatabaseRegistry registry) : IEventBatchValidator
{
    public EventBatch Validate(string database, EventBatch batch)
    {
        DatabaseEntry entry = registry.Get(database);
        if (!entry.WriteAvailable)
        {
            throw new EventStoreUnavailableException(
                $"Database '{database}' is not accepting writes while in state '{entry.Status}'.");
        }

        CandidateEvent[] events = batch.Events.Select(candidate => Validate(entry, candidate)).ToArray();
        return batch with { Events = events };
    }

    private static CandidateEvent Validate(DatabaseEntry database, CandidateEvent candidate)
    {
        if (!database.Catalog.EventSchemas.TryGetValue(candidate.Type, out SchemaCatalogEntry? registration))
        {
            return candidate;
        }

        RegisteredJsonSchema schema = RegisteredJsonSchema.Parse(
            registration.Name, registration.DocumentJson, eventSchema: true);
        EventKey[] expected = schema.ExtractKeys(candidate.Data);
        if (expected.Length != candidate.Keys.Count || expected.Any(key => !candidate.Keys.Contains(key)))
        {
            throw new ArgumentException(
                $"Event '{candidate.Type}' consistency keys do not match its registered schema.");
        }

        return candidate with { Keys = expected };
    }
}