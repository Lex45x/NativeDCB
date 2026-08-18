using System.Text.Json;

namespace NativeDCB.Engine;

public static class StateFileInspector
{
    public static async Task<StateFileInspection> InspectAsync(
        string directory,
        int partitionNumber,
        CancellationToken cancellationToken = default)
    {
        if (partitionNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(partitionNumber));
        }

        string root = Path.GetFullPath(directory);
        string path = Path.Combine(root, $"state_{partitionNumber:000000}_v1.json");
        if (!File.Exists(path))
        {
            return new StateFileInspection(Present: false, Locked: false, JsonValid: false, SchemaValid: false,
                partitionNumber, Error: null);
        }

        try
        {
            await using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            StateFileModel? state;
            try
            {
                state = await JsonSerializer.DeserializeAsync<StateFileModel>(
                    stream, StorageJson.Options, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
            }
            catch (JsonException exception)
            {
                return new StateFileInspection(Present: true, Locked: false, JsonValid: false, SchemaValid: false,
                    partitionNumber, exception.Message);
            }

            if (state is null)
            {
                return new StateFileInspection(Present: true, Locked: false, JsonValid: false, SchemaValid: false,
                    partitionNumber, "The state file is empty.");
            }

            DatabaseMetadata metadata = await ReadMetadataAsync(root, cancellationToken)
                .ConfigureAwait(continueOnCapturedContext: false);
            // Malformed JSON can set a non-nullable event collection to null.
            // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
            bool schemaValid = state.FormatVersion == 1 &&
                               state.StoreId == metadata.StoreId &&
                               state.PartitionNumber == partitionNumber &&
                               state.Events is not null &&
                               string.Equals(state.EventSnapshotFingerprint,
                                   StateBuilder.ComputeEventSnapshotFingerprint(state.Events),
                                   StringComparison.Ordinal) &&
                               string.Equals(state.EventSchemaFingerprint, StateBuilder.EventSchemaFingerprint,
                                   StringComparison.Ordinal) &&
                               string.Equals(state.KeyEncodingFingerprint, StateBuilder.KeyEncodingFingerprint,
                                   StringComparison.Ordinal) &&
                               string.Equals(state.StateSchemaFingerprint, StateBuilder.StateSchemaFingerprint,
                                   StringComparison.Ordinal);
            return new StateFileInspection(Present: true, Locked: false, JsonValid: true, schemaValid, partitionNumber,
                schemaValid ? null : "The state file metadata or schema fingerprints do not match the database.");
        }
        catch (IOException exception)
        {
            return new StateFileInspection(Present: true, Locked: true, JsonValid: false, SchemaValid: false,
                partitionNumber, exception.Message);
        }
    }

    private static async Task<DatabaseMetadata> ReadMetadataAsync(
        string directory,
        CancellationToken cancellationToken)
    {
        string path = Path.Combine(directory, "database_v1.json");
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<DatabaseMetadata>(
                   stream, StorageJson.Options, cancellationToken).ConfigureAwait(continueOnCapturedContext: false)
               ?? throw new InvalidDataException($"Database metadata '{path}' is invalid.");
    }
}