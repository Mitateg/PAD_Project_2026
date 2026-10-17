using System.Text.Json;
using Pad.Common;
using Pad.Common.Grpc;

namespace Pad.Sender;

/// <summary>
/// Construieste mesaje conform contractului din Protos/broker.proto.
///
/// Partea 2 (RPC): nu mai construim un obiect C# pe care il serializam noi in JSON,
/// ci direct un BrokerMessage generat de protoc din fisierul .proto.
/// Serializarea pe fir o face gRPC (protobuf binar), nu noi.
///
/// Payload-ul ramane JSON, dar acum calatoreste ca un simplu string in campul payload_json:
/// protobuf nu are un tip "obiect JSON oarecare", deci il trimitem ca text.
/// </summary>
public static class MessageFactory
{
    /// <summary>Mesaj cu textul scris de utilizator.</summary>
    public static BrokerMessage FromText(string messageType, string text, string? correlationId = null)
    {
        return new BrokerMessage
        {
            // In proto3 campurile nu au valori implicite "inteligente", deci le completam noi
            // pe toate aici. Daca unul lipseste, broker-ul raspunde NACK missing_field:...
            MessageId = Guid.NewGuid().ToString(),
            CorrelationId = correlationId ?? Guid.NewGuid().ToString(),
            MessageType = messageType,
            SchemaVersion = 1,
            OccurredAt = DateTime.UtcNow.ToString("o"),
            PayloadJson = JsonSerializer.Serialize(new { text }, Json.Options),
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
