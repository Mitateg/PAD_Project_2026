using System.Text.Json;
using Pad.Common;
using Pad.Common.Grpc;

namespace Pad.Sender;

/// <summary>
/// Construieste mesaje conform contractului (contracts/message-v1.md).
/// Payload-ul este mereu de forma {"text": "..."}: simplu de citit in consola receiver-ului.
/// </summary>
public static class MessageFactory
{
    /// <summary>Mesaj cu textul scris de utilizator.</summary>
    public static BrokerMessage FromText(string messageType, string text, string? correlationId = null)
    {
        return new BrokerMessage
        {
            CorrelationId = correlationId ?? Guid.NewGuid().ToString(),
            MessageType = messageType,
            PayloadJson = JsonSerializer.SerializeToElement(new { text }, Json.Options),
        };
    }

    /// <summary>Mesaj generat automat, cu un text de proba potrivit tipului.</summary>
    public static BrokerMessage Sample(string messageType, string? correlationId = null)
    {
        string text = messageType switch
        {
            "Anunt"  => "Maine nu avem laborator",
            "Alerta" => "Serverul de la facultate este picat",
            _        => $"Salut {messageType}, acesta este un mesaj privat de test",
        };

        return FromText(messageType, text, correlationId);
    }
}
