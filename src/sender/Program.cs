using Pad.Common;
using Pad.Sender;

// Punctul de intrare al sender-ului: un meniu simplu in consola.
//   1. mesaje automate (unul pe fiecare tip public + unul privat pentru fiecare receiver conectat)
//   2. scriu eu textul, apoi aleg destinatia: Anunt, Alerta sau numele unui receiver (privat)
//   0. iesire

SenderOptions options;
try
{
    options = SenderOptions.Parse(args);
}
catch (Exception ex) when (ex is ArgumentException || ex is FormatException || ex is IndexOutOfRangeException)
{
    Console.WriteLine($"Argumente gresite: {ex.Message}");
    return 2;
}

Console.Title = "sender " + options.Name;
var log = new Logger(options.Name);
using var broker = new BrokerClient(options.Host, options.Port, options.Name, log);

// Asteptam broker-ul: daca nu ruleaza inca, reincercam pana porneste (Ctrl+C ca sa renunti).
broker.ConnectWithWait();

while (true)
{
    Console.WriteLine();
    Console.WriteLine("=== SENDER ===");
    Console.WriteLine("  1. Trimite mesaje automate (cate unul pentru fiecare destinatie)");
    Console.WriteLine("  2. Scriu eu mesajul");
    Console.WriteLine("  0. Iesire");
    Console.Write("alegere> ");

    string? choice = Console.ReadLine()?.Trim();

    if (choice == "0" || choice is null)
        break;

    if (choice == "1")
        SendAutomatic();
    else if (choice == "2")
        SendManual();
    else
        Console.WriteLine("Alege 1, 2 sau 0.");
}

return 0;

// ---------------------------------------------------------------------------

// Destinatiile posibile chiar acum: tipurile publice + un canal privat pentru fiecare receiver conectat.
List<string> Destinations()
{
    var destinations = new List<string>(Constants.KnownMessageTypes);
    destinations.AddRange(broker.ListReceivers());
    return destinations;
}

// Un mesaj pentru fiecare destinatie, toate cu acelasi correlationId (sunt "o singura actiune").
void SendAutomatic()
{
    string correlationId = Guid.NewGuid().ToString();

    foreach (string destination in Destinations())
        SendAndShow(MessageFactory.Sample(destination, correlationId));
}

// Utilizatorul scrie textul, apoi alege destinatia din lista.
void SendManual()
{
    Console.Write("Textul mesajului> ");
    string? text = Console.ReadLine()?.Trim();
    if (string.IsNullOrEmpty(text))
    {
        Console.WriteLine("Mesaj gol, nu trimit nimic.");
        return;
    }

    List<string> destinations = Destinations();
    Console.WriteLine("Catre cine?");
    for (int i = 0; i < destinations.Count; i++)
    {
        bool isPublic = Constants.KnownMessageTypes.Contains(destinations[i]);
        string label = isPublic ? $"{destinations[i]} (toti abonatii)" : $"\"{destinations[i]}\" (privat)";
        Console.WriteLine($"  {i + 1}. {label}");
    }
    Console.Write("destinatie> ");

    string? answer = Console.ReadLine()?.Trim();
    if (!int.TryParse(answer, out int index) || index < 1 || index > destinations.Count)
    {
        Console.WriteLine("Destinatie invalida, nu trimit nimic.");
        return;
    }

    SendAndShow(MessageFactory.FromText(destinations[index - 1], text));
}

// Afisam JSON-ul exact asa cum pleaca pe fir, apoi trimitem si asteptam ACK.
void SendAndShow(Message message)
{
    bool isPublic = Constants.KnownMessageTypes.Contains(message.MessageType);
    string destination = isPublic ? $"toti abonatii la {message.MessageType}" : $"\"{message.MessageType}\" (privat)";

    Console.WriteLine();
    Console.WriteLine($"Trimit catre {destination}: " + Json.Serialize(message));

    bool delivered = broker.SendWithRetry(message);
    Console.WriteLine(delivered ? "  -> ACK primit, mesajul este la broker." : "  -> NU s-a putut livra (vezi logul).");
}
