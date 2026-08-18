using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using NativeDCB.Engine.Storage.EventLog;
using NativeDCB.Model;
using NativeDCB.Model.Events;

namespace NativeDCB.Engine.Storage.Indexes;

internal static class IndexFileStore
{
    public static string GetPath(string directory, string eventType, EventKey key)
    {
        string eventHash = Hash(eventType);
        string keyHash = Hash($"{key.Name}\0{key.Value}");
        return Path.Combine(directory, $"index_{eventHash}_{keyHash}_v1.json");
    }

    public static async Task<IndexFileModel?> TryReadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 16_384,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<IndexFileModel>(
            stream, StorageJson.Options, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
    }

    public static async Task WriteAsync(
        string path,
        IndexFileModel model,
        CancellationToken cancellationToken)
    {
        string temporary = path + ".tmp";
        await using (FileStream stream = new(
                         temporary,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         bufferSize: 16_384,
                         FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(
                stream, model, StorageJson.Options, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, path, overwrite: true);
    }

    private static string Hash(string value)
    {
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..16];
    }
}