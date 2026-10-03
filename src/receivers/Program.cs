using Pad.Common;
using Pad.Receiver;

// Punctul de intrare al receiver-ului: conexiunea la broker ruleaza pe un thread de fundal,
// iar in consola avem un meniu pentru abonare / dezabonare la tipurile publice.
// Canalul privat (numele receiver-ului) este mereu activ si nu apare in meniul de dezabonare.

ReceiverOptions options;
try
{
    options = ReceiverOptions.Parse(args);
}
catch (Exception ex) when (ex is ArgumentException || ex is FormatException || ex is IndexOutOfRangeException)
{
    Console.WriteLine($"Argumente gresite: {ex.Message}");
    return 2;
}

// Fara --name, intrebam cum sa-l cheme. Broker-ul adauga un numar ca sa fie unic ("ion" -> "ion-1").
if (!options.NameGiven)
{
    Console.Write("Cum te cheama? (Enter = consumer)> ");
    string? typed = Console.ReadLine()?.Trim();
    options.Name = string.IsNullOrEmpty(typed) ? "consumer" : typed;
}

// Logurile tehnice merg doar in fisier (logs/<name>.log), ca sa nu se amestece cu meniul.
var log = new Logger(options.Name, toConsole: false);

// Procesorul se creeaza dupa ce broker-ul ne da numele unic (fisierul de dedup poarta numele unic).
// (declarata inainte, pentru ca PrintMenu, folosit in lambda, se refera la ea)
BrokerConnection connection = null!;
MessageProcessor? processor = null;
connection = new BrokerConnection(options, log, assignedName =>
{
    processor = new MessageProcessor(assignedName, options.Deduplicate, log) { AfterMessageShown = PrintMenu };
    return processor;
});

Console.WriteLine("Se conecteaza la broker...");
connection.Start();
connection.WaitForFirstConnection();

string[] publicTypes = Constants.KnownMessageTypes;

while (true)
{
    PrintMenu();
    string? choice = Console.ReadLine()?.Trim();

    if (choice == "0" || choice is null)
    {
        connection.Stop(); // inchidem frumos socketul, ca broker-ul sa vada o deconectare normala
        Console.WriteLine("La revedere.");
        break;
    }

    if (choice == "1")
    {
        // Poti sa te abonezi doar la tipurile publice la care NU esti deja abonat.
        string? type = ChooseFrom(publicTypes.Except(connection.PublicSubscriptions).ToList(), "Deja esti abonat la toate.");
        if (type is not null) { connection.Subscribe(type); Console.WriteLine($"[abonat la {type}]"); }
    }
    else if (choice == "2")
    {
        // Doar abonarile publice; canalul privat nu apare aici, nu te poti dezabona de la el.
        string? type = ChooseFrom(connection.PublicSubscriptions.ToList(), "Nu esti abonat la niciun tip public.");
        if (type is not null) { connection.Unsubscribe(type); Console.WriteLine($"[dezabonat de la {type}]"); }
    }
    else
    {
        Console.WriteLine("Alege 1, 2 sau 0.");
    }
}

return 0;

// ---------------------------------------------------------------------------

void PrintMenu()
{
    Console.WriteLine();
    var subscriptions = new List<string> { $"privat: {connection.AssignedName}" };
    subscriptions.AddRange(connection.PublicSubscriptions);
    Console.WriteLine($"=== RECEIVER \"{connection.AssignedName}\" === mesaje primite: {processor?.ProcessedCount ?? 0}");
    Console.WriteLine($"abonat la: [{string.Join(", ", subscriptions)}]");
    Console.WriteLine("  1. Aboneaza-ma la un tip");
    Console.WriteLine("  2. Dezaboneaza-ma de la un tip");
    Console.WriteLine("  0. Iesire");
    Console.Write("alegere> ");
}

string? ChooseFrom(List<string> items, string emptyMessage)
{
    if (items.Count == 0)
    {
        Console.WriteLine(emptyMessage);
        return null;
    }

    Console.WriteLine("Ce tip?");
    for (int i = 0; i < items.Count; i++)
        Console.WriteLine($"  {i + 1}. {items[i]}");
    Console.Write("tip> ");

    string? answer = Console.ReadLine()?.Trim();
    if (int.TryParse(answer, out int index) && index >= 1 && index <= items.Count)
        return items[index - 1];

    Console.WriteLine("Alegere invalida.");
    return null;
}