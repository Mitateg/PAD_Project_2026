using Pad.Common;

namespace Pad.Sender;

/// <summary>
/// Optiunile din linia de comanda. Toate sunt optionale, fara ele apare meniul.
///
///   dotnet run --project src/sender
///   dotnet run --project src/sender -- --name sender-2 --host 127.0.0.1 --port 4242
///   dotnet run --project src/sender -- --brokers localhost:4242,localhost:4243
///
/// Acelasi format de --brokers il foloseste si receiver-ul, ca sa pornim tot sistemul la fel.
/// </summary>
public class SenderOptions
{
    /// <summary>
    /// Adresa broker-ului. Poate fi IP sau nume de host.
    /// Implicit se ia din variabila de mediu PAD_BROKER_HOST (asa o seteaza docker-compose,
    /// unde broker-ul se numeste "broker"), altfel 127.0.0.1.
    /// </summary>
    public string Host { get; set; } = Environment.GetEnvironmentVariable("PAD_BROKER_HOST") ?? "127.0.0.1";
    public int Port { get; set; } = Constants.BrokerPort;

    /// <summary>
    /// Numele cerut. Broker-ul ii adauga un numar ca sa fie unic: "sender" -> "sender-1",
    /// al doilea sender pornit primeste "sender-2".
    /// </summary>
    public string Name { get; set; } = "sender";

    // Lista de brokere (PAD_BROKER_ADDRESSES sau --brokers). Daca e goala, folosim Host:Port.
    private List<string> _brokers = ParseAddresses(Environment.GetEnvironmentVariable("PAD_BROKER_ADDRESSES"));

    /// <summary>
    /// Adresele gRPC ale brokerelor, in ordinea in care se incearca.
    /// Cu mai multe adrese avem load balancing: daca primul broker nu raspunde (UNAVAILABLE),
    /// sender-ul trece la urmatorul din lista.
    /// </summary>
    public IReadOnlyList<string> BrokerAddresses =>
        _brokers.Count > 0 ? _brokers : new[] { $"http://{Host}:{Port}" };

    public static SenderOptions Parse(string[] args)
    {
        var options = new SenderOptions();

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--brokers": options._brokers = ParseAddresses(args[++i]); break;
                case "--host":    options.Host = args[++i]; break;
                case "--port":    options.Port = int.Parse(args[++i]); break;
                case "--name":    options.Name = args[++i]; break;
                default:
                    throw new ArgumentException($"Optiune necunoscuta: {args[i]}");
            }
        }

        return options;
    }

    /// <summary>
    /// "localhost:4242,localhost:4243" -> ["http://localhost:4242", "http://localhost:4243"].
    /// Daca adresa are deja schema (http://...), o lasam asa cum e.
    /// </summary>
    private static List<string> ParseAddresses(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new List<string>();

        return text
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(a => a.Contains("://") ? a : "http://" + a)
            .ToList();
    }
}
