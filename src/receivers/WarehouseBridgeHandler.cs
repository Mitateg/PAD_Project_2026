using System.Text;
using System.Text.Json;
using Pad.Common;
using Pad.Common.Grpc;

namespace Pad.Receiver;

public sealed class WarehouseBridgeHandler : IMessageHandler
{
    private readonly HttpClient _http;
    private readonly string _proxyUrl;

    // id-urile mesajelor deja duse la warehouse (deduplicare dupa messageId)
    private readonly HashSet<string> _done = new();
    private readonly object _lock = new();

    public WarehouseBridgeHandler(HttpClient http, string proxyUrl)
    {
        _http = http;
        _proxyUrl = proxyUrl.TrimEnd('/');
    }

    public async Task<bool> HandleAsync(BrokerMessage m, bool isPrivate)
    {
        lock (_lock)
        {
            if (_done.Contains(m.MessageId))
            {
                Console.WriteLine($"[bridge] duplicat ignorat ({m.MessageId})");
                return true;
            }
        }

        bool ack = await HandleCoreAsync(m);

        // id-ul se noteaza abia dupa ce stim rezultatul: la un 5xx intoarcem false,
        // iar reincercarea trebuie sa treaca din nou prin PUT, nu sa fie luata drept duplicat
        if (ack)
        {
            lock (_lock)
                _done.Add(m.MessageId);
        }

        return ack;
    }

    private async Task<bool> HandleCoreAsync(BrokerMessage m)
    {
        // payload stricat: nu se repara prin reincercare -> ACK
        string employeeJson;
        try
        {
            using var doc = JsonDocument.Parse(m.PayloadJson);
            if (!doc.RootElement.TryGetProperty("employee", out var emp))
            {
                Console.WriteLine("[bridge] payload fara 'employee', ignorat");
                return true;
            }
            employeeJson = emp.GetRawText();
        }
        catch (JsonException ex)
        {
            Console.WriteLine($"[bridge] payload invalid, ignorat: {ex.Message}");
            return true;
        }

        try
        {
            using var content = new StringContent(employeeJson, Encoding.UTF8, "application/json");
            using var resp = await _http.PutAsync($"{_proxyUrl}/employee/", content);
            int code = (int)resp.StatusCode;

            if (code is >= 200 and < 300) return true;          // reusit -> ACK

            if (code is >= 400 and < 500)                         // refuz de fond -> ACK
            {
                Console.WriteLine($"[bridge] {code}, date respinse, nu mai reincercam");
                return true;
            }

            Console.WriteLine($"[bridge] {code}, eroare temporara -> fara ACK");
            return false;                                         // 5xx -> brokerul reincearca
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Console.WriteLine($"[bridge] proxy indisponibil: {ex.Message}");
            return false;                                         // transport -> fara ACK
        }
    }
}