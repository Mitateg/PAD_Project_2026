using System.Text.Json.Serialization;

namespace Pad.Common;

/// <summary>
/// Raspunsul broker-ului dupa fiecare mesaj primit (ACK sau NACK).
/// Acelasi format il foloseste si receiver-ul cand confirma broker-ului ca a procesat mesajul.
/// </summary>
public class BrokerReply
{
    /// <summary>"ACK" sau "NACK".</summary>
    public string Status { get; set; } = "";

    /// <summary>Id-ul mesajului confirmat. Poate fi null daca JSON-ul primit nu s-a putut citi.</summary>
    public string? MessageId { get; set; }

    /// <summary>Motivul, doar la NACK: invalid_json, missing_field:xxx, unknown_type.</summary>
    public string? Reason { get; set; }

    /// <summary>Doar in raspunsul la HELLO-ul unui receiver: numele unic pe care i l-a dat broker-ul (ex. "ion-1").</summary>
    public string? AssignedName { get; set; }

    public static BrokerReply Ack(string messageId) =>
        new() { Status = Constants.StatusAck, MessageId = messageId };

    public static BrokerReply Nack(string? messageId, string reason) =>
        new() { Status = Constants.StatusNack, MessageId = messageId, Reason = reason };

    /// <summary>Ajutor pentru cod; nu face parte din JSON.</summary>
    [JsonIgnore]
    public bool IsAck => Status == Constants.StatusAck;
}
