using Pad.Client;
using Pad.Common.Models;

// Clientul HTTP al laboratorului 3.
//
// Cere datele din warehouse, dar NUMAI prin proxy - asta e cerinta etapei 2.
// Dupa fiecare raspuns afiseaza X-Cache si X-Served-By, deci se vede pe viu
// cum lucreaza cache-ul si round-robin-ul.

ClientOptions options;
try
{
    options = ClientOptions.Parse(args);
}
catch (Exception ex) when (ex is ArgumentException || ex is IndexOutOfRangeException)
{
    Console.WriteLine($"Argumente gresite: {ex.Message}");
    Console.WriteLine();
    Console.WriteLine("Folosire: dotnet run --project src/client -- [--url http://localhost:8080]");
    Console.WriteLine("                                           [--format json|xml] [--id nume] [--from cine@unde]");
    return 2;
}

var client = new WarehouseClient(options);
Console.Title = "client " + options.ClientId;

while (true)
{
    Console.WriteLine();
    Console.WriteLine($"=== CLIENT === (format: {options.Format.ToUpperInvariant()} · {options.Url})");
    Console.WriteLine("  1. Cauta un angajat dupa id");
    Console.WriteLine("  2. Listeaza angajati (offset / limit)");
    Console.WriteLine("  3. Schimba formatul (JSON / XML)");
    Console.WriteLine("  4. Ce s-a schimbat de la un moment dat");
    Console.WriteLine("  5. Trimite un angajat (PUT)");
    Console.WriteLine("  0. Iesire");
    Console.Write("alegere> ");

    string? choice = Console.ReadLine()?.Trim();

    if (choice == "0" || choice is null)
        break;

    switch (choice)
    {
        case "1": await GetOneAsync(); break;
        case "2": await ListAsync(); break;
        case "3": ToggleFormat(); break;
        case "4": await UpdatesAsync(); break;
        case "5": await PutAsync(); break;
        default: Console.WriteLine("Alege 1, 2, 3, 4, 5 sau 0."); break;
    }
}

return 0;

// ---------------------------------------------------------------------------

async Task GetOneAsync()
{
    Console.Write("id> ");
    if (!TryReadInt(out int id, minimum: 1))
    {
        Console.WriteLine("Id invalid (numar mai mare ca 0).");
        return;
    }

    await client.GetEmployeeAsync(id);
}

async Task ListAsync()
{
    Console.Write("offset (Enter = 0)> ");
    int offset = ReadIntOrDefault(0);

    Console.Write("limit (Enter = 10)> ");
    int limit = ReadIntOrDefault(10);

    await client.GetEmployeesAsync(offset, limit);
}

void ToggleFormat()
{
    options.Format = options.Format == "json" ? "xml" : "json";
    Console.WriteLine($"  -> format: {options.Format.ToUpperInvariant()}");
    Console.WriteLine("     (acelasi lucru cerut in alt format trebuie sa dea X-Cache: MISS prima data,");
    Console.WriteLine("      pentru ca antetul Accept face parte din cheia de cache a proxy-ului)");
}

async Task UpdatesAsync()
{
    Console.Write("de acum cate minute (Enter = 5)> ");
    int minutes = ReadIntOrDefault(5);

    DateTime since = DateTime.UtcNow.AddMinutes(-Math.Abs(minutes));
    Console.WriteLine($"  If-Modified-Since: {since.ToString("r")}");

    await client.GetUpdatesAsync(since);
}

async Task PutAsync()
{
    Console.Write("id> ");
    if (!TryReadInt(out int id, minimum: 1))
    {
        Console.WriteLine("Id invalid (numar mai mare ca 0).");
        return;
    }

    Console.Write("nume> ");
    string? name = Console.ReadLine()?.Trim();
    if (string.IsNullOrEmpty(name))
    {
        Console.WriteLine("Numele este obligatoriu.");
        return;
    }

    Console.Write("pozitie> ");
    string position = Console.ReadLine()?.Trim() ?? "";
    Console.Write("departament> ");
    string department = Console.ReadLine()?.Trim() ?? "";

    await client.PutEmployeeAsync(new Employee
    {
        Id = id,
        Name = name,
        Position = position,
        Department = department,
        // UpdatedAt ramane gol: warehouse-ul pune momentul primirii.
    });

    Console.WriteLine("  (dupa un PUT reusit, proxy-ul trebuie sa stearga raspunsurile din cache:");
    Console.WriteLine("   urmatorul GET pe acelasi angajat va fi din nou MISS)");
}

// ---------------------------------------------------------------------------

bool TryReadInt(out int value, int minimum)
{
    bool ok = int.TryParse(Console.ReadLine()?.Trim(), out value) && value >= minimum;
    return ok;
}

int ReadIntOrDefault(int fallback)
{
    string? text = Console.ReadLine()?.Trim();
    if (string.IsNullOrEmpty(text))
        return fallback;

    return int.TryParse(text, out int value) ? value : fallback;
}
