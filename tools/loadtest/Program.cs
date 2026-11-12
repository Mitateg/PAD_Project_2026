using System.Collections.Concurrent;
using System.Diagnostics;

// Unealta de incarcare pentru Lab 3.
//
//   dotnet run --project src/loadtest -- --clients 50 --requests 20 --path "/employee/?id=1"
//
// Porneste --clients sarcini in paralel, fiecare trimite --requests cereri GET prin proxy,
// si masoara: cat a durat, cate pe secunda, latentele, cat s-a servit din cache (X-Cache)
// si cum s-au impartit cererile pe noduri (X-Served-By).
//
// Ruleaza de doua ori: prima data cu cache-ul gol, a doua oara cu el cald. Diferenta
// dintre cele doua rulari este demonstratia pentru caching.

string url = (Environment.GetEnvironmentVariable("PAD_PROXY_URL") ?? "http://localhost:8080").TrimEnd('/');
string path = "/employee/?id=1";
string accept = "application/json";
int clients = 10;
int requests = 10;

try
{
    for (int i = 0; i < args.Length; i++)
    {
        if (args[i].StartsWith("--") && i + 1 >= args.Length)
            throw new ArgumentException($"Lipseste valoarea pentru {args[i]}");

        switch (args[i])
        {
            case "--url":      url = args[++i].TrimEnd('/'); break;
            case "--path":     path = args[++i]; break;
            case "--clients":  clients = RequirePositive(args[++i], "--clients"); break;
            case "--requests": requests = RequirePositive(args[++i], "--requests"); break;
            case "--accept":
                string value = args[++i].ToLowerInvariant();
                accept = value is "xml" or "application/xml" ? "application/xml" : "application/json";
                break;
            default:
                throw new ArgumentException($"Optiune necunoscuta: {args[i]}");
        }
    }
}
catch (Exception ex) when (ex is ArgumentException || ex is IndexOutOfRangeException)
{
    Console.WriteLine($"Argumente gresite: {ex.Message}");
    Console.WriteLine();
    Console.WriteLine("Folosire: dotnet run --project src/loadtest -- [--url http://localhost:8080]");
    Console.WriteLine("            [--clients 50] [--requests 20] [--path \"/employee/?id=1\"] [--accept json|xml]");
    return 2;
}

int total = clients * requests;

Console.WriteLine($"Tinta   : {url}{path}");
Console.WriteLine($"Format  : {accept}");
Console.WriteLine($"Plan    : {clients} clienti x {requests} cereri = {total} cereri");
Console.WriteLine();
Console.WriteLine("Rulez...");

// Rezultatele se aduna din mai multe fire deodata, deci colectii thread-safe.
var latencies = new ConcurrentBag<double>();
var cacheStates = new ConcurrentDictionary<string, int>();
var nodes = new ConcurrentDictionary<string, int>();
var statuses = new ConcurrentDictionary<int, int>();
int errors = 0;

// Un singur HttpClient pentru toate firele: e proiectat sa fie refolosit.
// Cate unul per cerere ar epuiza socketurile si masuratoarea ar iesi gresita.
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", accept);

var totalTime = Stopwatch.StartNew();

await Parallel.ForEachAsync(
    Enumerable.Range(0, clients),
    new ParallelOptions { MaxDegreeOfParallelism = clients },
    async (clientIndex, token) =>
    {
        for (int i = 0; i < requests; i++)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url + path);
                request.Headers.TryAddWithoutValidation("X-Client-Id", $"load-{clientIndex}");
                request.Headers.TryAddWithoutValidation("From", "loadtest@pad.local");

                using HttpResponseMessage response = await http.SendAsync(request, token);
                await response.Content.ReadAsStringAsync(token);
                stopwatch.Stop();

                latencies.Add(stopwatch.Elapsed.TotalMilliseconds);
                statuses.AddOrUpdate((int)response.StatusCode, 1, (_, c) => c + 1);
                Count(cacheStates, Header(response, "X-Cache"));
                Count(nodes, Header(response, "X-Served-By"));
            }
            catch (Exception)
            {
                // Orice esec (timeout, conexiune refuzata) se numara, dar nu opreste testul:
                // exact in timpul unei caderi de nod vrem sa vedem ce se intampla.
                Interlocked.Increment(ref errors);
            }
        }
    });

totalTime.Stop();

// --------------------------------------------------------------------- raport

var sorted = latencies.OrderBy(x => x).ToList();
double seconds = totalTime.Elapsed.TotalSeconds;

Console.WriteLine();
Console.WriteLine("================ REZULTATE ================");
Console.WriteLine($"Total      : {total} cereri in {seconds:F1} s  ->  {total / Math.Max(seconds, 0.001):F0} cereri/s");

if (sorted.Count > 0)
{
    Console.WriteLine($"Latenta    : p50 = {Percentile(sorted, 50):F0} ms"
                    + $"   p95 = {Percentile(sorted, 95):F0} ms"
                    + $"   max = {sorted[^1]:F0} ms");
}

if (errors > 0)
    Console.WriteLine($"Erori      : {errors}");

Console.WriteLine($"Coduri     : {Describe(statuses.ToDictionary(k => k.Key.ToString(), v => v.Value))}");
Console.WriteLine($"Cache      : {Describe(cacheStates)}");
Console.WriteLine($"Noduri     : {Describe(nodes)}");
Console.WriteLine("===========================================");

if (nodes.Count == 1 && nodes.ContainsKey("-"))
{
    Console.WriteLine();
    Console.WriteLine("Nota: nu au venit antetele X-Cache / X-Served-By.");
    Console.WriteLine("      Inseamna ca ai lovit direct un warehouse, nu proxy-ul.");
}

return 0;

// --------------------------------------------------------------------- ajutoare

static int RequirePositive(string text, string option)
{
    if (!int.TryParse(text, out int value) || value < 1)
        throw new ArgumentException($"{option} trebuie sa fie un numar mai mare ca 0");
    return value;
}

static string Header(HttpResponseMessage response, string name) =>
    response.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : "-";

static void Count(ConcurrentDictionary<string, int> target, string key) =>
    target.AddOrUpdate(key, 1, (_, c) => c + 1);

/// <summary>Percentila dintr-o lista deja sortata crescator.</summary>
static double Percentile(List<double> sortedValues, int percent)
{
    if (sortedValues.Count == 0)
        return 0;

    int index = (int)Math.Ceiling(percent / 100.0 * sortedValues.Count) - 1;
    return sortedValues[Math.Clamp(index, 0, sortedValues.Count - 1)];
}

/// <summary>"HIT 962 (96%) · MISS 38 (4%)", ordonat descrescator.</summary>
static string Describe(IDictionary<string, int> counts)
{
    if (counts.Count == 0)
        return "-";

    int sum = counts.Values.Sum();
    return string.Join("  ·  ", counts
        .OrderByDescending(pair => pair.Value)
        .Select(pair => $"{pair.Key} {pair.Value} ({100.0 * pair.Value / sum:F0}%)"));
}
