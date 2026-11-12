namespace Pad.Client;

/// <summary>
/// Optiunile din linia de comanda ale clientului.
///
///   dotnet run --project src/client
///   dotnet run --project src/client -- --url http://localhost:8080 --format xml --id client-ana
///
/// Adresa implicita este proxy-ul. Clientul NU trebuie sa vorbeasca direct cu un warehouse:
/// cerinta etapei 2 este ca tot traficul sa treaca prin proxy. Optiunea --url exista totusi,
/// ca sa putem testa si direct pe un warehouse cat timp proxy-ul nu e gata.
/// </summary>
public class ClientOptions
{
    /// <summary>Adresa proxy-ului. Implicit din PAD_PROXY_URL, altfel localhost:8080.</summary>
    public string Url { get; set; } =
        Environment.GetEnvironmentVariable("PAD_PROXY_URL") ?? "http://localhost:8080";

    /// <summary>"json" sau "xml". Se poate schimba si din meniu.</summary>
    public string Format { get; set; } = "json";

    /// <summary>Trimis ca antet X-Client-Id, ca proxy-ul sa ne poata numara in Redis.</summary>
    public string ClientId { get; set; } = "client-" + Environment.MachineName.ToLowerInvariant();

    /// <summary>Trimis ca antet From, ca in schema din enunt (nodename@hostdomain).</summary>
    public string From { get; set; } = "client@pad.local";

    public static ClientOptions Parse(string[] args)
    {
        var options = new ClientOptions();

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--") && i + 1 >= args.Length)
                throw new ArgumentException($"Lipseste valoarea pentru {args[i]}");

            switch (args[i])
            {
                case "--url":    options.Url = args[++i].TrimEnd('/'); break;
                case "--format": options.Format = NormalizeFormat(args[++i]); break;
                case "--id":     options.ClientId = args[++i]; break;
                case "--from":   options.From = args[++i]; break;
                default:
                    throw new ArgumentException($"Optiune necunoscuta: {args[i]}");
            }
        }

        return options;
    }

    /// <summary>Acceptam "json", "xml", dar si forma completa "application/json".</summary>
    public static string NormalizeFormat(string value)
    {
        string v = value.Trim().ToLowerInvariant();
        return v switch
        {
            "json" or "application/json" => "json",
            "xml" or "application/xml" or "text/xml" => "xml",
            _ => throw new ArgumentException($"Format necunoscut: {value} (acceptate: json, xml)"),
        };
    }

    /// <summary>Valoarea pentru antetul Accept, pornind de la formatul ales.</summary>
    public string AcceptHeader => Format == "xml" ? "application/xml" : "application/json";
}
