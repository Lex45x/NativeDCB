using System.Reflection;
using System.Text;

namespace NativeDCB.Commerce;

public static class CommerceNdl
{
    private const string ResourceName = "NativeDCB.Commerce.NativeCommerce.ndl";

    public static async Task<string> LoadAsync(CancellationToken cancellationToken = default)
    {
        Assembly assembly = typeof(CommerceNdl).Assembly;
        await using Stream stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded Commerce NDL resource '{ResourceName}' was not found in '{assembly.FullName}'.");
        using StreamReader reader = new(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            detectEncodingFromByteOrderMarks: true);

        string source;
        try
        {
            source = await reader.ReadToEndAsync(cancellationToken);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException(
                $"Embedded Commerce NDL resource '{ResourceName}' is not valid UTF-8.", exception);
        }

        if (string.IsNullOrWhiteSpace(source))
        {
            throw new InvalidDataException($"Embedded Commerce NDL resource '{ResourceName}' is empty.");
        }

        return source;
    }
}
