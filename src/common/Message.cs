using System.Text.Json;

namespace Pad.Common;

/// <summary>
/// Mesajul care circula prin sistem (vezi contracts/message-v1.md).
/// Sender-ul il construieste, broker-ul il ruteaza dupa MessageType,
/// receiver-ul il proceseaza.
/// </summary>
public class Message
{
    /// <summary>Id unic per mesaj. Receiver-ul il foloseste pentru deduplicare.</summary>
    public string MessageId { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Leaga toate mesajele unei solicitari. Apare in toate logurile.</summary>
    public string CorrelationId { get; set; } = "";

    /// <summary>Tipul mesajului (Mesaj, Anunt, Alerta). Dupa el se face rutarea.</summary>
    public string MessageType { get; set; } = "";

    /// <summary>Versiunea contractului. Incepem cu 1.</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Momentul producerii, ISO-8601 in UTC.</summary>
    public string OccurredAt { get; set; } = DateTime.UtcNow.ToString("o");

    /// <summary>Continutul propriu-zis, un obiect JSON oarecare.</summary>
    public JsonElement Payload { get; set; }
}
