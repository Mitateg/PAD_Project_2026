using Pad.Common;
using Xunit;

namespace Pad.Common.Tests;

public class MessageValidatorTests
{
    private const string Valid =
        """{"messageId":"m1","correlationId":"c1","messageType":"Anunt","schemaVersion":1,"occurredAt":"2026-09-14T10:00:00Z","payload":{"orderId":1}}""";

    [Fact]
    public void AnuntValid_TreceValidarea()
    {
        bool ok = MessageValidator.TryParse(Valid, out var message, out var reason);

        Assert.True(ok);
        Assert.Null(reason);
        Assert.Equal("m1", message!.MessageId);
    }

    [Fact]
    public void JsonInvalid_ReturneazaInvalidJson()
    {
        bool ok = MessageValidator.TryParse("nu e json", out var message, out var reason);

        Assert.False(ok);
        Assert.Null(message);
        Assert.Equal("invalid_json", reason);
    }

    [Fact]
    public void FaraMessageType_ReturneazaMissingField()
    {
        string json = Valid.Replace("\"messageType\":\"Anunt\",", "");

        MessageValidator.TryParse(json, out _, out var reason);

        Assert.Equal("missing_field:messageType", reason);
    }

    [Fact]
    public void FaraPayload_ReturneazaMissingField()
    {
        string json = Valid.Replace(",\"payload\":{\"orderId\":1}", "");

        MessageValidator.TryParse(json, out _, out var reason);

        Assert.Equal("missing_field:payload", reason);
    }

    [Fact]
    public void TipNecunoscut_ReturneazaUnknownType()
    {
        string json = Valid.Replace("Anunt", "OrderDeleted");

        MessageValidator.TryParse(json, out _, out var reason);

        Assert.Equal("unknown_type", reason);
    }
}
