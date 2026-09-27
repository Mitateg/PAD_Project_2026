using System.Text.Json;
using Pad.Common;
using Xunit;

namespace Pad.Common.Tests;

public class MessageSerializationTests
{
    [Fact]
    public void Serialize_ProduceCampuriCamelCase_PeOSinguraLinie()
    {
        var message = new Message
        {
            CorrelationId = "corr-1",
            MessageType = "Anunt",
            Payload = JsonDocument.Parse("""{"orderId":42}""").RootElement,
        };

        string json = Json.Serialize(message);

        Assert.Contains("\"messageId\"", json);
        Assert.Contains("\"messageType\":\"Anunt\"", json);
        Assert.Contains("\"payload\":{\"orderId\":42}", json);
        Assert.DoesNotContain("\n", json); // framing-ul cere o singura linie
    }

    [Fact]
    public void Serialize_ApoiDeserialize_PastreazaToateCampurile()
    {
        var original = new Message
        {
            MessageId = "id-1",
            CorrelationId = "corr-1",
            MessageType = "Anunt",
            SchemaVersion = 1,
            OccurredAt = "2026-09-14T10:00:00.0000000Z",
            Payload = JsonDocument.Parse("""{"amount":10.5}""").RootElement,
        };

        var copy = Json.TryDeserialize<Message>(Json.Serialize(original));

        Assert.NotNull(copy);
        Assert.Equal(original.MessageId, copy!.MessageId);
        Assert.Equal(original.CorrelationId, copy.CorrelationId);
        Assert.Equal(original.MessageType, copy.MessageType);
        Assert.Equal(original.SchemaVersion, copy.SchemaVersion);
        Assert.Equal(original.OccurredAt, copy.OccurredAt);
        Assert.Equal(10.5, copy.Payload.GetProperty("amount").GetDouble());
    }

    [Fact]
    public void TryDeserialize_JsonStricat_ReturneazaNull()
    {
        var result = Json.TryDeserialize<Message>("{ nu e json");

        Assert.Null(result);
    }

    [Fact]
    public void BrokerReply_Ack_SeSerializeazaConformContractului()
    {
        string json = Json.Serialize(BrokerReply.Ack("id-7"));

        Assert.Equal("""{"status":"ACK","messageId":"id-7"}""", json);
    }
}
