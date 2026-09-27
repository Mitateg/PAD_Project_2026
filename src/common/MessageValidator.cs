using System.Text.Json;

namespace Pad.Common;

/// <summary>
/// Verifica daca o linie primita este un mesaj valid conform contractului.
/// Este folosit de broker (inainte de ACK/NACK) si de teste.
/// </summary>
public static class MessageValidator
{
    /// <summary>
    /// Incearca sa transforme linia intr-un Message.
    /// Daca reuseste, message este completat si reason este null.
    /// Daca nu, message este null si reason contine motivul pentru NACK.
    /// </summary>
    public static bool TryParse(string line, out Message? message, out string? reason,
                                IEnumerable<string>? extraTypes = null)
    {
        message = null;

        var parsed = Json.TryDeserialize<Message>(line);
        if (parsed is null)
        {
            reason = "invalid_json";
            return false;
        }

        reason = Validate(parsed, extraTypes);
        if (reason is not null)
            return false;

        message = parsed;
        return true;
    }

    /// <summary>
    /// Returneaza null daca mesajul este valid, altfel motivul.
    /// extraTypes = tipuri acceptate in plus fata de cele publice (broker-ul da numele receiver-ilor = canale private).
    /// </summary>
    public static string? Validate(Message m, IEnumerable<string>? extraTypes = null)
    {
        if (string.IsNullOrWhiteSpace(m.MessageId))
            return "missing_field:messageId";

        if (string.IsNullOrWhiteSpace(m.CorrelationId))
            return "missing_field:correlationId";

        if (string.IsNullOrWhiteSpace(m.MessageType))
            return "missing_field:messageType";

        if (string.IsNullOrWhiteSpace(m.OccurredAt))
            return "missing_field:occurredAt";

        // Payload lipsa se deserializeaza ca JsonElement de tip Undefined.
        if (m.Payload.ValueKind == JsonValueKind.Undefined || m.Payload.ValueKind == JsonValueKind.Null)
            return "missing_field:payload";

        bool known = Constants.KnownMessageTypes.Contains(m.MessageType)
                  || (extraTypes is not null && extraTypes.Contains(m.MessageType));
        if (!known)
            return "unknown_type";

        return null;
    }
}
