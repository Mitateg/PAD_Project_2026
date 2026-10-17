using Google.Protobuf;
using Pad.Common.Grpc;
using Xunit;

namespace Pad.Common.Tests;

public class MessageSerializationTests
{
     [Fact]
     public void BrokerMessage_SetSiGet_PastreazaToateCampurile()
     {
          var message = new BrokerMessage
          {
               MessageId = "id-1",
               CorrelationId = "corr-1",
               MessageType = "Anunt",
               SchemaVersion = 1,
               OccurredAt = "2026-09-14T10:00:00.0000000Z",
               PayloadJson = """{"amount":10.5}""",
          };

          Assert.Equal("id-1", message.MessageId);
          Assert.Equal("corr-1", message.CorrelationId);
          Assert.Equal("Anunt", message.MessageType);
          Assert.Equal(1, message.SchemaVersion);
          Assert.Equal("2026-09-14T10:00:00.0000000Z", message.OccurredAt);
          Assert.Equal("""{"amount":10.5}""", message.PayloadJson);
     }

     [Fact]
     public void BrokerMessage_ProtobufSerializeDeserialize_PastreazaCampurile()
     {
          var original = new BrokerMessage
          {
               MessageId = "id-1",
               CorrelationId = "corr-1",
               MessageType = "Anunt",
               SchemaVersion = 1,
               OccurredAt = "2026-09-14T10:00:00Z",
               PayloadJson = """{"orderId":42}""",
          };

          // Serializare protobuf binara si inapoi
          byte[] bytes = original.ToByteArray();
          var copy = BrokerMessage.Parser.ParseFrom(bytes);

          Assert.Equal(original.MessageId, copy.MessageId);
          Assert.Equal(original.CorrelationId, copy.CorrelationId);
          Assert.Equal(original.MessageType, copy.MessageType);
          Assert.Equal(original.SchemaVersion, copy.SchemaVersion);
          Assert.Equal(original.OccurredAt, copy.OccurredAt);
          Assert.Equal(original.PayloadJson, copy.PayloadJson);
     }

     [Fact]
     public void SendReply_Ack_ConformContractului()
     {
          var reply = new SendReply
          {
               Status = Constants.StatusAck,
               MessageId = "id-7"
          };

          Assert.Equal("ACK", reply.Status);
          Assert.Equal("id-7", reply.MessageId);
          Assert.Empty(reply.Reason);
     }

     [Fact]
     public void SendReply_Nack_CuMotiv()
     {
          var reply = new SendReply
          {
               Status = Constants.StatusNack,
               MessageId = "id-8",
               Reason = "unknown_type"
          };

          Assert.Equal("NACK", reply.Status);
          Assert.Equal("id-8", reply.MessageId);
          Assert.Equal("unknown_type", reply.Reason);
     }

     [Fact]
     public void Json_SerializeDeserialize_FunctioneazaCuObiecteSimple()
     {
          // Json.cs ramane neschimbat — verificam ca inca functioneaza pentru WAL
          var obj = new { messageId = "m1", text = "salut" };
          string json = Json.Serialize(obj);

          Assert.Contains("\"messageId\":\"m1\"", json);
          Assert.Contains("\"text\":\"salut\"", json);
          Assert.DoesNotContain("\n", json); // WriteIndented = false
     }
}