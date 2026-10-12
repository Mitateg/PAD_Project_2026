using Pad.Common.Grpc;

namespace Pad.Common;

/// <summary>
/// Verifica daca un BrokerMessage (protobuf) este valid conform contractului.
/// Folosit de broker inainte de ACK/NACK.
/// </summary>
public static class MessageValidator
{
     /// <summary>
     /// Returneaza null daca mesajul este valid, altfel motivul pentru NACK.
     /// extraTypes = tipuri acceptate in plus fata de cele publice (broker-ul da numele receiver-ilor = canale private).
     /// </summary>
     public static string? Validate(BrokerMessage m, IEnumerable<string>? extraTypes = null)
     {
          if (string.IsNullOrWhiteSpace(m.MessageId))
               return "missing_field:messageId";

          if (string.IsNullOrWhiteSpace(m.CorrelationId))
               return "missing_field:correlationId";

          if (string.IsNullOrWhiteSpace(m.MessageType))
               return "missing_field:messageType";

          if (string.IsNullOrWhiteSpace(m.OccurredAt))
               return "missing_field:occurredAt";

          if (string.IsNullOrWhiteSpace(m.PayloadJson))
               return "missing_field:payload";

          bool known = Constants.KnownMessageTypes.Contains(m.MessageType)
                    || (extraTypes is not null && extraTypes.Contains(m.MessageType));
          if (!known)
               return "unknown_type";

          return null;
     }
}
