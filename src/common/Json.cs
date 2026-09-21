using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pad.Common;

/// <summary>
/// Un singur loc unde configuram serializarea JSON, ca toate componentele sa scrie la fel:
/// nume de campuri camelCase (messageId, nu MessageId) si FARA indentare,
/// pentru ca un mesaj trebuie sa incapa pe o singura linie (framing pe newline).
/// </summary>
public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        // Campurile null (ex. "reason" la ACK) nu apar deloc in JSON.
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, Options);

    /// <summary>Returneaza null daca textul nu este JSON valid pentru tipul cerut.</summary>
    public static T? TryDeserialize<T>(string json) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
