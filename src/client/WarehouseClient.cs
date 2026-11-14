using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Xml.Linq;
using System.Xml.Serialization;
using Pad.Common;
using Pad.Common.Models;

namespace Pad.Client;

/// <summary>
/// Cererile HTTP catre proxy si afisarea raspunsului.
///
/// Dupa fiecare cerere aratam: codul de stare, cat a durat, si antetele puse de proxy
/// (X-Cache, X-Served-By). Fara ele nu s-ar vedea nimic din ce face proxy-ul:
/// aceeasi cerere de doua ori trebuie sa dea MISS, apoi HIT si un timp mult mai mic.
/// </summary>
public class WarehouseClient
{
    /// <summary>
    /// Doar pentru afisare in consola. Encoder-ul relaxat lasa &amp;, &lt; si diacriticele asa cum sunt,
    /// in loc sa le scrie ca &. E sigur aici: textul merge in terminal, nu intr-o pagina web.
    /// </summary>
    private static readonly JsonSerializerOptions PrettyJson = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly HttpClient _http;
    private readonly ClientOptions _options;

    public WarehouseClient(ClientOptions options)
    {
        _options = options;

        // Un singur HttpClient refolosit pentru toate cererile. Daca am crea unul nou
        // la fiecare apel, am ramane fara socketuri sub incarcare.
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    // ------------------------------------------------------------------ cereri

    public Task GetEmployeeAsync(int id) =>
        SendAsync(HttpMethod.Get, $"/employee/?id={id}");

    public Task GetEmployeesAsync(int offset, int limit) =>
        SendAsync(HttpMethod.Get, $"/employees/?offset={offset}&limit={limit}");

    /// <summary>
    /// Pull-request: ce s-a schimbat de la un moment incoace.
    /// Contractul cere antetul If-Modified-Since cu data in format HTTP, ca in enunt
    /// ("Sat, 29 Oct 2014 19:43:31 GMT"). Warehouse-ul accepta si ?since=ISO-8601.
    /// </summary>
    public Task GetUpdatesAsync(DateTime sinceUtc)
    {
        var headers = new Dictionary<string, string>
        {
            // "r" produce exact formatul RFC 1123 cerut de HTTP.
            ["If-Modified-Since"] = sinceUtc.ToUniversalTime().ToString("r"),
        };

        return SendAsync(HttpMethod.Get, "/update/employees/", extraHeaders: headers);
    }

    /// <summary>PUT = creare sau inlocuire. Il folosim ca sa aratam invalidarea cache-ului.</summary>
    public Task PutEmployeeAsync(Employee employee)
    {
        string body = _options.Format == "xml" ? ToXml(employee) : ToJson(employee);
        return SendAsync(HttpMethod.Put, "/employee/", body);
    }

    // ------------------------------------------------------------------ miezul

    private async Task SendAsync(HttpMethod method, string path, string? body = null,
                                 Dictionary<string, string>? extraHeaders = null)
    {
        using var request = new HttpRequestMessage(method, _options.Url + path);

        request.Headers.TryAddWithoutValidation("Accept", _options.AcceptHeader);
        request.Headers.TryAddWithoutValidation("X-Client-Id", _options.ClientId);
        request.Headers.TryAddWithoutValidation("From", _options.From);

        if (extraHeaders is not null)
            foreach (var (name, value) in extraHeaders)
                request.Headers.TryAddWithoutValidation(name, value);

        if (body is not null)
            request.Content = new StringContent(body, Encoding.UTF8, _options.AcceptHeader);

        Console.WriteLine();
        Console.WriteLine($"-> {method} {path}   (Accept: {_options.AcceptHeader})");

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using HttpResponseMessage response = await _http.SendAsync(request);
            stopwatch.Stop();

            string content = await response.Content.ReadAsStringAsync();
            PrintResponse(response, content, stopwatch.ElapsedMilliseconds);
        }
        catch (TaskCanceledException)
        {
            // Timeout-ul nostru de 10s. Daca proxy-ul nu raspunde deloc, nu ramanem agatati.
            Console.WriteLine($"   TIMEOUT dupa {stopwatch.ElapsedMilliseconds} ms - proxy-ul nu a raspuns.");
        }
        catch (HttpRequestException ex)
        {
            // Cazul cel mai des la demo: proxy-ul nu e pornit.
            Console.WriteLine($"   NU M-AM PUTUT CONECTA la {_options.Url}");
            Console.WriteLine($"   ({ex.Message})");
            Console.WriteLine("   Verifica daca proxy-ul ruleaza, sau porneste clientul cu --url.");
        }
    }

    private static void PrintResponse(HttpResponseMessage response, string content, long milliseconds)
    {
        string cache = HeaderOrDash(response, "X-Cache");
        string node = HeaderOrDash(response, "X-Served-By");

        Console.WriteLine($"   {(int)response.StatusCode} {response.StatusCode}"
                        + $"  ·  {milliseconds} ms"
                        + $"  ·  X-Cache: {cache}"
                        + $"  ·  X-Served-By: {node}");

        // Explicatii scurte pentru codurile care apar des, ca sa nu para cod misterios.
        switch (response.StatusCode)
        {
            case HttpStatusCode.NotFound:
                Console.WriteLine("   (angajatul nu exista)");
                return;
            case HttpStatusCode.NotAcceptable:
                Console.WriteLine("   (formatul cerut nu este suportat de warehouse)");
                return;
            case HttpStatusCode.UnsupportedMediaType:
                Console.WriteLine("   (Content-Type-ul trimis nu este acceptat)");
                return;
        }

        if (string.IsNullOrWhiteSpace(content))
            return;

        Console.WriteLine();
        Console.WriteLine(Pretty(content));
    }

    private static string HeaderOrDash(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : "-";

    // ------------------------------------------------------------------ afisare

    /// <summary>Formateaza frumos raspunsul, fie el JSON sau XML. Daca nu e niciuna, il lasa asa.</summary>
    private static string Pretty(string content)
    {
        string trimmed = content.TrimStart();

        try
        {
            if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
            {
                using JsonDocument document = JsonDocument.Parse(content);
                return JsonSerializer.Serialize(document.RootElement, PrettyJson);
            }

            if (trimmed.StartsWith('<'))
                return XDocument.Parse(content).ToString();
        }
        catch (Exception ex) when (ex is JsonException || ex is System.Xml.XmlException)
        {
            // Nu e JSON/XML valid: il aratam asa cum a venit, e mai util decat o eroare.
        }

        return content;
    }

    // ------------------------------------------------------------------ corpuri

    private static string ToJson(Employee employee) =>
        JsonSerializer.Serialize(employee, Json.Options);

    private static string ToXml(Employee employee)
    {
        var serializer = new XmlSerializer(typeof(Employee));
        using var writer = new StringWriter();
        serializer.Serialize(writer, employee);
        return writer.ToString();
    }
}
