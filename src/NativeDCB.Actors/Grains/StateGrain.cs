using System.Diagnostics;
using System.Globalization;

using NativeDCB.Actors.Audit;
using NativeDCB.Actors.Contracts;
using NativeDCB.Actors.Messages;
using NativeDCB.Actors.Observability;
using NativeDCB.Actors.Storage;
using NativeDCB.Engine.Storage.EventLog;
using NativeDCB.Engine.Storage.State;

namespace NativeDCB.Actors.Grains;

// ReSharper disable once UnusedType.Global -- Orleans activates grains by interface at runtime.
public sealed class StateGrain(ActorStoragePath storage, IGrainFactory grains) : Grain, IStateGrain
{
    public async Task<StateFileStatusMessage> InspectAsync(GrainCancellationToken cancellationToken)
    {
        (string database, int partitionNumber) = StateGrainKey.Parse(this.GetPrimaryKeyString());
        string directory = storage.GetDatabaseDirectory(database);
        StateFileInspection inspection = await StateFileInspector.InspectAsync(
                directory, partitionNumber, cancellationToken.CancellationToken)
            .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
        return new StateFileStatusMessage(
            inspection.Present,
            inspection.Locked,
            inspection.JsonValid,
            inspection.SchemaValid,
            inspection.SourcePartition,
            inspection.Error);
    }

    public Task RebuildAsync()
    {
        return BuildAsync();
    }

    public Task RequestRebuildAsync()
    {
        return BuildObservedAsync();
    }

    private async Task BuildObservedAsync()
    {
        long started = Stopwatch.GetTimestamp();
        try
        {
            await BuildAsync().ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
        catch (Exception)
        {
            RecordMaintenance("faulted", started);
            await AuditRecorder.OutcomeAsync(grains, "faulted", "unexpected")
                .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
            throw;
        }

        RecordMaintenance("completed", started);
        await AuditRecorder.OutcomeAsync(grains, "completed")
            .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
    }

    private Task BuildAsync()
    {
        (string database, int partitionNumber) = StateGrainKey.Parse(this.GetPrimaryKeyString());
        string directory = storage.GetDatabaseDirectory(database);
        StateBuilder builder = new(new DatabaseOptions(directory));
        return builder.BuildAsync(partitionNumber);
    }

    private static void RecordMaintenance(string outcome, long started)
    {
        ActorTelemetry.MaintenanceOperations.Add(1,
            new KeyValuePair<string, object?>("kind", "state_rebuild"),
            new KeyValuePair<string, object?>("outcome", outcome));
        ActorTelemetry.MaintenanceDuration.Record(
            Stopwatch.GetElapsedTime(started).TotalSeconds,
            new KeyValuePair<string, object?>("kind", "state_rebuild"),
            new KeyValuePair<string, object?>("outcome", outcome));
    }
}

internal static class StateGrainKey
{
    private const char Separator = '|';

    public static string Create(string database, int partitionNumber)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{database}{Separator}{partitionNumber}");
    }

    public static (string Database, int PartitionNumber) Parse(string key)
    {
        int separator = key.LastIndexOf(Separator);
        if (separator <= 0 ||
            !int.TryParse(key.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture,
                out int partitionNumber) ||
            partitionNumber <= 0)
        {
            throw new InvalidOperationException($"State grain key '{key}' is invalid.");
        }

        return (key[..separator], partitionNumber);
    }
}