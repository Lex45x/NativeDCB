using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Options;

using NativeDCB.Actors.Catalog;
using NativeDCB.Actors.Messages;

namespace NativeDCB.Actors.Decisions.Remote;

public sealed class RemoteDecisionTokenProtector
{
    private const byte EnvelopeVersion = 1;
    private const int HeaderLength = 11;
    private const int MacLength = 32;
    private const int MaximumPayloadLength = 1024 * 1024;
    private static readonly byte[] Magic = "NDCB"u8.ToArray();

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly string? _activeKeyId;
    private readonly IReadOnlyDictionary<string, byte[]> _keys;
    private readonly TimeProvider _timeProvider;

    public RemoteDecisionTokenProtector(IOptions<RemoteDecisionOptions> options, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
        RemoteDecisionOptions configured = options.Value;
        Lifetime = configured.Lifetime;

        Dictionary<string, byte[]> keys = new(StringComparer.Ordinal);
        try
        {
            foreach ((string keyId, string encoded) in configured.SigningKeys)
            {
                if (string.IsNullOrWhiteSpace(keyId))
                {
                    throw new InvalidOperationException("Remote decision signing key IDs must be non-empty.");
                }

                byte[] key = Convert.FromBase64String(encoded);
                if (key.Length < 32)
                {
                    throw new InvalidOperationException(
                        $"Remote decision signing key '{keyId}' must contain at least 32 bytes.");
                }

                keys.Add(keyId, key);
            }

            if (Lifetime <= TimeSpan.Zero)
            {
                throw new InvalidOperationException("Remote decision token lifetime must be positive.");
            }

            try
            {
                _ = _timeProvider.GetUtcNow().Add(Lifetime);
            }
            catch (ArgumentOutOfRangeException exception)
            {
                throw new InvalidOperationException(
                    "Remote decision token lifetime exceeds the supported date range.", exception);
            }

            if (string.IsNullOrWhiteSpace(configured.ActiveKeyId) ||
                !keys.ContainsKey(configured.ActiveKeyId))
            {
                throw new InvalidOperationException(
                    "Remote decisions require an ActiveKeyId that identifies a configured signing key.");
            }

            _activeKeyId = configured.ActiveKeyId;
            _keys = keys;
        }
        catch (Exception exception) when
            (exception is FormatException or ArgumentException or InvalidOperationException)
        {
            ConfigurationError = exception.Message;
            _keys = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        }
    }

    public bool IsConfigured => ConfigurationError is null;
    public string? ConfigurationError { get; }
    public TimeSpan Lifetime { get; }
    internal DateTimeOffset UtcNow => _timeProvider.GetUtcNow();

    internal byte[] Protect(RemoteDecisionClaimsMessage claims)
    {
        if (!IsConfigured || _activeKeyId is null)
        {
            throw new InvalidOperationException(ConfigurationError ?? "Remote decisions are not configured.");
        }

        byte[] keyId = StrictUtf8.GetBytes(_activeKeyId);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(claims, CatalogJson.Options);
        if (keyId.Length > ushort.MaxValue || payload.Length > MaximumPayloadLength)
        {
            throw new InvalidOperationException("The remote decision context is too large.");
        }

        int protectedLength = checked(HeaderLength + keyId.Length + payload.Length);
        byte[] envelope = new byte[checked(protectedLength + MacLength)];
        Magic.CopyTo(envelope, index: 0);
        envelope[4] = EnvelopeVersion;
        BinaryPrimitives.WriteUInt16BigEndian(envelope.AsSpan(start: 5, length: 2), checked((ushort)keyId.Length));
        BinaryPrimitives.WriteInt32BigEndian(envelope.AsSpan(start: 7, length: 4), payload.Length);
        keyId.CopyTo(envelope, HeaderLength);
        payload.CopyTo(envelope, HeaderLength + keyId.Length);
        HMACSHA256.HashData(_keys[_activeKeyId], envelope.AsSpan(start: 0, protectedLength),
            envelope.AsSpan(protectedLength, MacLength));
        return envelope;
    }

    internal bool TryUnprotect(ReadOnlySpan<byte> envelope, out RemoteDecisionClaimsMessage? claims)
    {
        claims = null;
        try
        {
            if (envelope.Length < HeaderLength + MacLength ||
                !envelope[..Magic.Length].SequenceEqual(Magic) ||
                envelope[index: 4] != EnvelopeVersion)
            {
                return false;
            }

            int keyIdLength = BinaryPrimitives.ReadUInt16BigEndian(envelope.Slice(start: 5, length: 2));
            int payloadLength = BinaryPrimitives.ReadInt32BigEndian(envelope.Slice(start: 7, length: 4));
            if (payloadLength < 0 || payloadLength > MaximumPayloadLength)
            {
                return false;
            }

            int protectedLength = checked(HeaderLength + keyIdLength + payloadLength);
            if (envelope.Length != protectedLength + MacLength)
            {
                return false;
            }

            string keyId = StrictUtf8.GetString(envelope.Slice(HeaderLength, keyIdLength));
            if (!_keys.TryGetValue(keyId, out byte[]? key))
            {
                return false;
            }

            Span<byte> expected = stackalloc byte[MacLength];
            HMACSHA256.HashData(key, envelope[..protectedLength], expected);
            if (!CryptographicOperations.FixedTimeEquals(expected, envelope[protectedLength..]))
            {
                return false;
            }

            claims = JsonSerializer.Deserialize<RemoteDecisionClaimsMessage>(
                envelope.Slice(HeaderLength + keyIdLength, payloadLength), CatalogJson.Options);
            return claims is not null && Valid(claims);
        }
        catch (Exception exception) when (exception is JsonException or DecoderFallbackException or OverflowException)
        {
            return false;
        }
    }

    // Authenticated JSON can still violate nullable annotations after deserialization.
    // ReSharper disable ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
    private static bool Valid(RemoteDecisionClaimsMessage claims)
    {
        // ReSharper disable once MergeIntoPattern
        return !string.IsNullOrWhiteSpace(claims.Database) &&
               claims.StoreId != Guid.Empty &&
               claims.CommandId != Guid.Empty &&
               !string.IsNullOrWhiteSpace(claims.CommandType) &&
               !string.IsNullOrWhiteSpace(claims.HandlerName) &&
               !string.IsNullOrWhiteSpace(claims.PlanFingerprint) &&
               !string.IsNullOrWhiteSpace(claims.CommandJsonHash) &&
               !string.IsNullOrWhiteSpace(claims.ModelHash) &&
               claims.ObservedHead >= 0 &&
               claims.Query is not null &&
               claims.SchemaFingerprints is not null &&
               claims.ExpectedEventTypes is not null &&
               claims.ExpectedEventTypes.All(value => !string.IsNullOrWhiteSpace(value)) &&
               claims.Nonce is { Length: >= 16 } &&
               claims.ExpiresUtc > claims.IssuedUtc;
    }
    // ReSharper restore ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
}