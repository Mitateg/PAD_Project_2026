using Microsoft.AspNetCore.Server.Kestrel.Core;
using Pad.Common;
using Pad.Warehouse.Data;
using Pad.Warehouse.Services;
using Pad.Warehouse.Startup;

// Punct de intrare al warehouse-ului (DW): server HTTP care tine datele angajatilor.
//
// Variabile de mediu:
//   PAD_PORT             portul HTTP (implicit 5000)
//   PAD_CASSANDRA_HOST   daca e setat, datele stau in Cassandra; daca nu, in memorie
//   PAD_WAREHOUSE_NAME   numele din loguri (implicit "warehouse"); util cand ruleaza 3 noduri

string name = Environment.GetEnvironmentVariable("PAD_WAREHOUSE_NAME") ?? "warehouse";
int port = int.TryParse(Environment.GetEnvironmentVariable("PAD_PORT"), out int p) ? p : Constants.WarehousePort;
string? cassandraHost = Environment.GetEnvironmentVariable("PAD_CASSANDRA_HOST");

var log = new Logger(name);
var builder = WebApplication.CreateBuilder(args);

// Doar HTTP/1.1 (imaginile .NET 8 asculta implicit pe 8080, deci setam portul explicit).
builder.WebHost.ConfigureKestrel(options =>
    options.ListenAnyIP(port, listen => listen.Protocols = HttpProtocols.Http1));

builder.Services.AddSingleton(log);
builder.Services.AddWarehouseMvc();

// Singura linie care alege stocarea. Controller-ul si serviciul nu se ating.
if (!string.IsNullOrWhiteSpace(cassandraHost))
{
    log.Info("storage", result: $"cassandra @ {cassandraHost}");
    var session = await CassandraSession.ConnectAsync(cassandraHost, log);
    builder.Services.AddSingleton(session);
    builder.Services.AddSingleton<IEmployeeRepository, CassandraEmployeeRepository>();
}
else
{
    log.Info("storage", result: "in-memory (PAD_CASSANDRA_HOST nu este setat)");
    builder.Services.AddSingleton<IEmployeeRepository, InMemoryEmployeeRepository>();
}

// Serviciul nu tine stare intre cereri, deci un singur exemplar e suficient si sigur pe mai multe fire.
builder.Services.AddSingleton<EmployeeService>();

var app = builder.Build();

// Dovada pentru "un fir pe cerere": cate o linie in log pentru fiecare cerere, cu id-ul firului.
// La o rulare cu mai multi clienti simultan apar multe id-uri diferite.
// /health este apelat des de proxy si de Docker, deci nu-l scriem.
app.Use(async (context, next) =>
{
    if (context.Request.Path != "/health")
    {
        // Antetul From (ex. nodename@hostdomain) spune cine a trimis cererea, ca in schema din enunt.
        string from = context.Request.Headers["From"].ToString();
        string caller = string.IsNullOrEmpty(from) ? "" : $" from={from}";

        log.Info("http_request",
            result: $"{context.Request.Method} {context.Request.Path}{context.Request.QueryString} thread={Environment.CurrentManagedThreadId}{caller}");
    }
    await next();
});

app.MapControllers();

log.Info("warehouse_started", result: $"{name} pe portul {port}");
app.Run();
