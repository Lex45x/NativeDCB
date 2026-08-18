using System.Security.Cryptography;
using System.Text.Json;

using NativeDCB.Engine.Storage.EventLog;
using NativeDCB.Model;
using NativeDCB.Model.Events;

namespace NativeDCB.Engine.Storage.State;

internal static class StateCheckpointRecovery
{
    public static async Task<RecoveredLog?> TryLoadLatestAsync(
        string directory,
        Guid storeId,
        IReadOnlyList<(int Number, string Path)> partitions,
        CancellationToken cancellationToken)
    {
        foreach (string path in Directory.EnumerateFiles(directory, "state_*_v1.json")
                     .OrderByDescending(value => value, StringComparer.Ordinal))
        {
            try
            {
                await using FileStream stateStream = new(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 16_384,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                StateFileModel? state = await JsonSerializer.DeserializeAsync<StateFileModel>(
                        stateStream, StorageJson.Options, cancellationToken)
                    .ConfigureAwait(continueOnCapturedContext: false);
                if (!ValidMetadata(state, storeId, partitions.Count))
                {
                    continue;
                }

                bool valid = true;
                for (int index = 0; index < state!.Partitions.Count; index++)
                {
                    PartitionCheckpoint source = state.Partitions[index];
                    (int number, string partitionPath) = partitions[index];
                    if (source.PartitionNumber != number || new FileInfo(partitionPath).Length != source.Length)
                    {
                        valid = false;
                        break;
                    }

                    await using FileStream partitionStream = new(
                        partitionPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        bufferSize: 16_384,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    string fingerprint = Convert.ToHexString(
                            await SHA256.HashDataAsync(partitionStream, cancellationToken)
                                .ConfigureAwait(continueOnCapturedContext: false))
                        .ToLowerInvariant();
                    if (!string.Equals(fingerprint, source.ContentFingerprint, StringComparison.Ordinal))
                    {
                        valid = false;
                        break;
                    }
                }

                if (!valid || !ValidateEvents(state))
                {
                    continue;
                }

                RecoveredLog recovered = new();
                recovered.Events.AddRange(state.Events.Select(value => value with { Data = value.Data.Clone() }));
                foreach (IGrouping<Guid, SequencedEvent> command in recovered.Events.GroupBy(value => value.CommandId))
                {
                    recovered.Commands.Add(command.Key, command.ToList());
                }

                for (int index = 0; index < state.Partitions.Count; index++)
                {
                    PartitionCheckpoint source = state.Partitions[index];
                    recovered.Partitions.Add(new RecoveredPartition(
                        source.PartitionNumber,
                        partitions[index].Path,
                        source.FirstEventId,
                        source.LastEventId,
                        source.CommittedEventCount));
                }

                return recovered;
            }
            catch (JsonException)
            {
                // Derived state is optional; malformed checkpoints are ignored.
            }
            catch (IOException)
            {
                // A concurrent state build or transient derived-file issue cannot block recovery.
            }
        }

        return null;
    }

    // Malformed JSON can set non-nullable collection properties to null.
    // ReSharper disable ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
    private static bool ValidMetadata(
        StateFileModel? state,
        Guid storeId,
        int partitionCount)
    {
        return state is not null && state.FormatVersion == 1 && state.StoreId == storeId &&
               state.PartitionNumber > 0 && state.PartitionNumber < partitionCount &&
               state.Partitions is not null && state.Events is not null && state.KnownEventIds is not null &&
               state.Commands is not null && state.Partitions.Count == state.PartitionNumber &&
               state.Events.Count == state.CommittedEventCount && state.KnownEventIds.Count == state.Events.Count &&
               state.Head == (state.Events.Count == 0 ? 0 : state.Events[^1].EventId) &&
               string.Equals(state.EventSnapshotFingerprint,
                   StateBuilder.ComputeEventSnapshotFingerprint(state.Events), StringComparison.Ordinal) &&
               string.Equals(state.EventSchemaFingerprint, StateBuilder.EventSchemaFingerprint,
                   StringComparison.Ordinal) &&
               string.Equals(state.KeyEncodingFingerprint, StateBuilder.KeyEncodingFingerprint,
                   StringComparison.Ordinal) &&
               string.Equals(state.StateSchemaFingerprint, StateBuilder.StateSchemaFingerprint,
                   StringComparison.Ordinal);
    }
    // ReSharper restore ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract

    // Malformed JSON can contain a null key collection.
    // ReSharper disable ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
    private static bool ValidateEvents(StateFileModel state)
    {
        long expectedId = 1;
        foreach (SequencedEvent @event in state.Events)
        {
            if (@event.EventId != expectedId || state.KnownEventIds[(int)(expectedId - 1)] != expectedId ||
                @event.CommandId == Guid.Empty || string.IsNullOrWhiteSpace(@event.CommandType) ||
                string.IsNullOrWhiteSpace(@event.Type) || @event.SchemaVersion == 0 ||
                @event.Keys is null || @event.Keys.Count == 0)
            {
                return false;
            }

            expectedId++;
        }

        CommandState[] commands = state.Events.GroupBy(value => value.CommandId)
            .Select(group => new CommandState(group.Key, group.First().EventId, group.Last().EventId, group.Count()))
            .OrderBy(value => value.FirstEventId)
            .ToArray();
        return commands.SequenceEqual(state.Commands);
    }
    // ReSharper restore ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
}