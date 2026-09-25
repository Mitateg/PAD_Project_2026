using Pad.Common;

namespace Pad.Sender;

/// <summary>
/// Optiunile din linia de comanda. Toate sunt optionale, fara ele apare meniul.
///
///   dotnet run --project src/sender
///   dotnet run --project src/sender -- --name sender-2 --host 127.0.0.1 --port 4242
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

    public static SenderOptions Parse(string[] args)
    {
        var options = new SenderOptions();

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--host": options.Host = args[++i]; break;
                case "--port": options.Port = int.Parse(args[++i]); break;
                case "--name": options.Name = args[++i]; break;
                default:
                    throw new ArgumentException($"Optiune necunoscuta: {args[i]}");
            }
        }

        return options;
    }
}
