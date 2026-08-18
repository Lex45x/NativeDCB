using System.Text;

namespace NativeDCB.Engine.Actors.Messages;

internal sealed record IndexIdentity(string Database, string EventType, EventKeyMessage Key)
{
    public static string Encode(string database, string eventType, EventKeyMessage key)
    {
        return string.Join(separator: '.',
            EncodePart(database), EncodePart(eventType), EncodePart(key.Name), EncodePart(key.Value));
    }

    public static IndexIdentity Decode(string value)
    {
        string[] parts = value.Split(separator: '.');
        if (parts.Length != 4)
        {
            throw new InvalidOperationException("The index grain identity is invalid.");
        }

        return new IndexIdentity(
            DecodePart(parts[0]),
            DecodePart(parts[1]),
            new EventKeyMessage(DecodePart(parts[2]), DecodePart(parts[3])));
    }

    private static string EncodePart(string value)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd(trimChar: '=').Replace(oldChar: '+', newChar: '-').Replace(oldChar: '/', newChar: '_');
    }

    private static string DecodePart(string value)
    {
        string encoded = value.Replace(oldChar: '-', newChar: '+').Replace(oldChar: '_', newChar: '/');
        encoded = encoded.PadRight((encoded.Length + 3) / 4 * 4, paddingChar: '=');
        return Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
    }
}