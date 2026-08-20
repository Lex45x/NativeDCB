using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using NativeDCB.Engine.Storage.EventLog;
using NativeDCB.Engine.Storage.State;
using NativeDCB.Model;
using NativeDCB.Model.Events;

namespace NativeDCB.Engine.Storage.Indexes;

internal static class IndexFileStore
{
    public static string GetIndexDirectory(string databaseDirectory, string eventType, EventKey key)
    {
        string eventHash = Hash(eventType);
        string keyHash = Hash($"{key.Name}\0{key.Value}");
        return Path.Combine(databaseDirectory, "indexes", $"index_{eventHash}_{keyHash}");
    }

    public static string GetManifestPath(string databaseDirectory, string eventType, EventKey key)
    {
        return Path.Combine(GetIndexDirectory(databaseDirectory, eventType, key), "manifest_v1.json");
    }

    public static IEnumerable<string> EnumerateManifestPaths(string databaseDirectory)
    {
        string indexesDirectory = Path.Combine(databaseDirectory, "indexes");
        return Directory.Exists(indexesDirectory)
            ? Directory.EnumerateFiles(indexesDirectory, "manifest_v1.json", SearchOption.AllDirectories)
            : [];
    }

    public static Task<IndexManifestModel?> TryReadManifestAsync(
        string path,
        CancellationToken cancellationToken)
    {
        return TryReadAsync<IndexManifestModel>(path, cancellationToken);
    }

    public static async Task<IndexFileModel?> TryReadPublishedAsync(
        string databaseDirectory,
        string eventType,
        EventKey key,
        CancellationToken cancellationToken)
    {
        string manifestPath = GetManifestPath(databaseDirectory, eventType, key);
        IndexManifestModel? manifest = await TryReadManifestAsync(manifestPath, cancellationToken)
            .ConfigureAwait(continueOnCapturedContext: false);
        if (manifest is null)
        {
            return null;
        }

        ValidateManifest(manifest, eventType, key);
        string indexDirectory = Path.GetDirectoryName(manifestPath)!;
        if (string.IsNullOrEmpty(manifest.GenerationFile) ||
            !string.Equals(Path.GetFileName(manifest.GenerationFile), manifest.GenerationFile,
                StringComparison.Ordinal) ||
            !manifest.GenerationFile.StartsWith("generation_", StringComparison.Ordinal) ||
            !manifest.GenerationFile.EndsWith("_v1.json", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The index manifest references an invalid generation file.");
        }

        string generationPath = Path.Combine(indexDirectory, manifest.GenerationFile);
        IndexFileModel? model = await TryReadAsync<IndexFileModel>(generationPath, cancellationToken)
            .ConfigureAwait(continueOnCapturedContext: false);
        if (model is null)
        {
            throw new InvalidDataException("The index manifest references a missing generation file.");
        }

        ValidateGeneration(model, manifest);
        return model;
    }

    public static async Task PublishAsync(
        string databaseDirectory,
        IndexFileModel model,
        CancellationToken cancellationToken)
    {
        string indexDirectory = GetIndexDirectory(databaseDirectory, model.EventType, model.Key);
        Directory.CreateDirectory(indexDirectory);
        string generationFile = $"generation_{model.Head:D20}_{Guid.NewGuid():N}_v1.json";
        string generationPath = Path.Combine(indexDirectory, generationFile);
        await WriteNewAsync(generationPath, model, cancellationToken)
            .ConfigureAwait(continueOnCapturedContext: false);

        IndexManifestModel manifest = new(
            FormatVersion: 1,
            model.EventType,
            model.Key,
            model.Head,
            generationFile,
            model.EventSchemaFingerprint,
            model.KeyEncodingFingerprint);
        await ReplaceAsync(Path.Combine(indexDirectory, "manifest_v1.json"), manifest, cancellationToken)
            .ConfigureAwait(continueOnCapturedContext: false);
    }

    private static async Task<T?> TryReadAsync<T>(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return default;
        }

        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            bufferSize: 16_384,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<T>(
            stream, StorageJson.Options, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
    }

    private static async Task WriteNewAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 16_384,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await JsonSerializer.SerializeAsync(
            stream, value, StorageJson.Options, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        stream.Flush(flushToDisk: true);
    }

    private static async Task ReplaceAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        string temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await WriteNewAsync(temporary, value, cancellationToken)
                .ConfigureAwait(continueOnCapturedContext: false);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static void ValidateManifest(IndexManifestModel manifest, string eventType, EventKey key)
    {
        if (manifest.FormatVersion != 1 ||
            !string.Equals(manifest.EventType, eventType, StringComparison.Ordinal) ||
            manifest.Key is null ||
            manifest.Key != key ||
            manifest.Head < 0 ||
            !string.Equals(manifest.EventSchemaFingerprint, StateBuilder.EventSchemaFingerprint,
                StringComparison.Ordinal) ||
            !string.Equals(manifest.KeyEncodingFingerprint, StateBuilder.KeyEncodingFingerprint,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("The index manifest metadata or schema fingerprints are invalid.");
        }
    }

    private static void ValidateGeneration(IndexFileModel model, IndexManifestModel manifest)
    {
        if (model.FormatVersion != 1 ||
            !string.Equals(model.EventType, manifest.EventType, StringComparison.Ordinal) ||
            model.Key is null ||
            model.Key != manifest.Key ||
            model.Head != manifest.Head ||
            !string.Equals(model.EventSchemaFingerprint, manifest.EventSchemaFingerprint,
                StringComparison.Ordinal) ||
            !string.Equals(model.KeyEncodingFingerprint, manifest.KeyEncodingFingerprint,
                StringComparison.Ordinal) ||
            model.Events is null ||
            model.Events.Any(value => value is null) ||
            model.Events.Any(value => value.EventId > model.Head) ||
            model.Events.Select(value => value.EventId).Distinct().Count() != model.Events.Count ||
            !model.Events.SequenceEqual(model.Events.OrderBy(value => value.EventId)) ||
            model.Events.Any(value =>
                !string.Equals(value.Type, model.EventType, StringComparison.Ordinal) ||
                value.Keys is null ||
                !value.Keys.Contains(model.Key)))
        {
            throw new InvalidDataException("The index generation is inconsistent with its manifest.");
        }
    }

    private static string Hash(string value)
    {
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..16];
    }
}