using Pad.Common;
using Pad.Common.Grpc;
using Xunit;

namespace Pad.Common.Tests;

public class MessageValidatorTests
{
     /// <summary>Helper: creeaza un BrokerMessage valid cu toate campurile.</summary>
     private static BrokerMessage ValidMessage() => new()
     {
          MessageId = "m1",
          CorrelationId = "c1",
          MessageType = "Anunt",
          SchemaVersion = 1,
          OccurredAt = "2026-09-14T10:00:00Z",
          PayloadJson = """{"orderId":1}""",
     };

     [Fact]
     public void AnuntValid_TreceValidarea()
     {
          var message = ValidMessage();

          string? reason = MessageValidator.Validate(message);

          Assert.Null(reason);
     }

     [Fact]
     public void AlertaValida_TreceValidarea()
     {
          var message = ValidMessage();
          message.MessageType = "Alerta";

          string? reason = MessageValidator.Validate(message);

          Assert.Null(reason);
     }

     [Fact]
     public void FaraMessageId_ReturneazaMissingField()
     {
          var message = ValidMessage();
          message.MessageId = "";

          string? reason = MessageValidator.Validate(message);

          Assert.Equal("missing_field:messageId", reason);
     }

     [Fact]
     public void FaraCorrelationId_ReturneazaMissingField()
     {
          var message = ValidMessage();
          message.CorrelationId = "";

          string? reason = MessageValidator.Validate(message);

          Assert.Equal("missing_field:correlationId", reason);
     }

     [Fact]
     public void FaraMessageType_ReturneazaMissingField()
     {
          var message = ValidMessage();
          message.MessageType = "";

          string? reason = MessageValidator.Validate(message);

          Assert.Equal("missing_field:messageType", reason);
     }

     [Fact]
     public void FaraOccuredAt_ReturneazaMissingField()
     {
          var message = ValidMessage();
          message.OccurredAt = "";

          string? reason = MessageValidator.Validate(message);

          Assert.Equal("missing_field:occurredAt", reason);
     }

     [Fact]
     public void FaraPayload_ReturneazaMissingField()
     {
          var message = ValidMessage();
          message.PayloadJson = "";

          string? reason = MessageValidator.Validate(message);

          Assert.Equal("missing_field:payload", reason);
     }

     [Fact]
     public void TipNecunoscut_ReturneazaUnknownType()
     {
          var message = ValidMessage();
          message.MessageType = "OrderDeleted";

          string? reason = MessageValidator.Validate(message);

          Assert.Equal("unknown_type", reason);
     }

     [Fact]
     public void TipPrivat_AcceptatCuExtraTypes()
     {
          var message = ValidMessage();
          message.MessageType = "ion-1"; // canal privat = numele receiver-ului

          string? reason = MessageValidator.Validate(message, extraTypes: new[] { "ion-1" });

          Assert.Null(reason);
     }

     [Fact]
     public void TipPrivat_RespinsFaraExtraTypes()
     {
          var message = ValidMessage();
          message.MessageType = "ion-1";

          string? reason = MessageValidator.Validate(message);

          Assert.Equal("unknown_type", reason);
     }
}