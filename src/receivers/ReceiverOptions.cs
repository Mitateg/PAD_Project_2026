namespace Pad.Receiver;

/// <summary>
/// Optiunile de pornire ale receiver-ului, citite din argumentele liniei de comanda:
///   --name, --host, --port, --subscribe (Anunt,Alerta), --no-dedup, --crash-before-ack
///
/// NOTA: valorile implicite de host/port trebuie verificate fata de broker-ul real
/// (vezi docker-compose.yml sau src/broker/Program.cs).
/// </summary>
public class ReceiverOptions
{
    public string Name { get; set; } = "consumer";

    /// <summary>False daca numele nu a fost dat prin --name; Program intreaba interactiv in acest caz.</summary>
    public bool NameGiven { get; private set; }

    public string Host { get; set; } = Environment.GetEnvironmentVariable("PAD_BROKER_HOST") ?? "localhost";
    public int Port { get; set; } = 4242;

    public List<string> Subscriptions { get; set; } = new();
    public bool Deduplicate { get; set; } = true;
    public bool CrashBeforeAck { get; set; }

    public static ReceiverOptions Parse(string[] args)
    {
        var options = new ReceiverOptions();

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--name":
                    options.Name = RequireValue(args, ref i, "--name");
                    options.NameGiven = true;
                    break;

                case "--host":
                    options.Host = RequireValue(args, ref i, "--host");
                    break;

                case "--port":
                    string portValue = RequireValue(args, ref i, "--port");
                    if (!int.TryParse(portValue, out int port))
                        throw new FormatException($"Port invalid: '{portValue}'");
                    options.Port = port;
                    break;

                case "--subscribe":
                    string subsValue = RequireValue(args, ref i, "--subscribe");
                    options.Subscriptions = subsValue
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .ToList();
                    break;

                case "--no-dedup":
                    options.Deduplicate = false;
                    break;

                case "--crash-before-ack":
                    options.CrashBeforeAck = true;
                    break;

                default:
                    throw new ArgumentException($"Argument necunoscut: '{args[i]}'");
            }
        }

        return options;
    }

    private static string RequireValue(string[] args, ref int i, string flagName)
    {
        if (i + 1 >= args.Length)
            throw new IndexOutOfRangeException($"Lipseste valoarea pentru {flagName}");
        i++;
        return args[i];
    }
}
