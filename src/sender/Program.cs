using Pad.Common;
using Pad.Common.Grpc;
using Pad.Common.Models;
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

// Logul se creeaza dupa conectare, cu numele primit de la broker (ex. "sender-1"),
// ca doi senderi porniti odata sa scrie in fisiere diferite.
using var broker = new BrokerClient(options.BrokerAddresses, options.Name,
                                    assignedName => new Logger(assignedName, toConsole: false));

// Asteptam broker-ul: daca nu ruleaza inca, reincercam pana porneste (Ctrl+C ca sa renunti).
broker.ConnectWithWait();
Console.Title = "sender " + broker.Name;

// Folosit de optiunile 3 si 5 ca sa genereze angajati de proba.
Random employeeRandom = new();

while (true)
{
    Console.WriteLine();
    Console.WriteLine(broker.HasFailover
        ? $"=== SENDER \"{broker.Name}\" === (broker: {broker.CurrentAddress})"
        : $"=== SENDER \"{broker.Name}\" ===");
    Console.WriteLine("  1. Trimite mesaje automate (cate unul pentru fiecare destinatie)");
    Console.WriteLine("  2. Scriu eu mesajul");
    Console.WriteLine("  3. Trimite un angajat generat automat        (EmployeeChange)");
    Console.WriteLine("  4. Scriu eu datele angajatului               (EmployeeChange)");
    Console.WriteLine("  5. Trimite N angajati deodata                (EmployeeChange)");
    Console.WriteLine("  0. Iesire");
    Console.Write("alegere> ");

    string? choice = Console.ReadLine()?.Trim();

    if (choice == "0" || choice is null)
        break;

    if (choice == "1")
        SendAutomatic();
    else if (choice == "2")
        SendManual();
    else if (choice == "3")
        SendRandomEmployee();
    else if (choice == "4")
        SendTypedEmployee();
    else if (choice == "5")
        SendManyEmployees();
    else
        Console.WriteLine("Alege 1, 2, 3, 4, 5 sau 0.");
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

// Trimitem mesajul si asteptam ACK. In consola aratam doar unde a plecat si rezultatul,
// fara JSON-ul complet (acela ramane in logul sender-ului, daca e nevoie de el).
void SendAndShow(BrokerMessage message)
{
    bool isPublic = Constants.KnownMessageTypes.Contains(message.MessageType);
    string destination = isPublic ? $"toti abonatii la {message.MessageType}" : $"\"{message.MessageType}\" (privat)";

    Console.WriteLine();
    Console.WriteLine($"Trimit catre {destination}...");

    bool delivered = broker.SendWithRetry(message);
    Console.WriteLine(delivered ? "  -> ACK primit, mesajul este la broker." : "  -> NU s-a putut livra (vezi logul).");
}

// ---------------------------------------------------------------------------
// Lab 3: mesaje EmployeeChange. Pleaca prin broker ca orice alt mesaj public,
// iar bridge-ul receiver le duce mai departe la warehouse printr-un PUT.


// Un angajat generat automat.
void SendRandomEmployee()
{
    Employee employee = MessageFactory.SampleEmployee(employeeRandom);
    Console.WriteLine($"  angajat: #{employee.Id} {employee.Name}, {employee.Position}, {employee.Department}");
    SendAndShow(MessageFactory.EmployeeChange(employee));
}

// Utilizatorul scrie datele angajatului.
void SendTypedEmployee()
{
    Console.Write("id (numar)> ");
    if (!int.TryParse(Console.ReadLine()?.Trim(), out int id) || id <= 0)
    {
        Console.WriteLine("Id invalid (trebuie numar mai mare ca 0), nu trimit nimic.");
        return;
    }

    Console.Write("nume> ");
    string? name = Console.ReadLine()?.Trim();
    if (string.IsNullOrEmpty(name))
    {
        Console.WriteLine("Numele este obligatoriu, nu trimit nimic.");
        return;
    }

    Console.Write("pozitie> ");
    string position = Console.ReadLine()?.Trim() ?? "";
    Console.Write("departament> ");
    string department = Console.ReadLine()?.Trim() ?? "";

    SendAndShow(MessageFactory.EmployeeChange(new Employee
    {
        Id = id,
        Name = name,
        Position = position,
        Department = department,
    }));
}

// N angajati deodata, toti cu acelasi correlationId: in loguri se vede ca sunt o singura actiune.
void SendManyEmployees()
{
    Console.Write("cati angajati> ");
    if (!int.TryParse(Console.ReadLine()?.Trim(), out int count) || count < 1 || count > 500)
    {
        Console.WriteLine("Numar invalid (1-500), nu trimit nimic.");
        return;
    }

    string correlationId = Guid.NewGuid().ToString();
    int delivered = 0;

    for (int i = 0; i < count; i++)
    {
        Employee employee = MessageFactory.SampleEmployee(employeeRandom);
        if (broker.SendWithRetry(MessageFactory.EmployeeChange(employee, correlationId)))
            delivered++;
    }

    Console.WriteLine();
    Console.WriteLine($"Trimise {delivered} din {count}. Toate au correlationId = {correlationId}");
    if (delivered < count)
        Console.WriteLine("  -> cele nelivrate sunt in logul sender-ului (message_failed).");
}
