using Pad.Common;
using Pad.Broker;

// Punct de intrare al brokerului.

int port = Constants.BrokerPort;
string name = "broker";
string? walPath = null;

for (int i = 0; i < args.Length; i++)
{
     switch (args[i])
     {
          case "--port":
               if (i + 1 < args.Length && int.TryParse(args[++i], out int p))
                    port = p;
               else
                    throw new ArgumentException("Lipseste sau e invalid valoarea pentru --port");
               break;
          case "--name":
               if (i + 1 < args.Length)
                    name = args[++i];
               else
                    throw new ArgumentException("Lipseste valoarea pentru --name");
               break;
          case "--wal":
               if (i + 1 < args.Length)
                    walPath = args[++i];
               else
                    throw new ArgumentException("Lipseste valoarea pentru --wal");
               break;
          default:
               throw new ArgumentException($"Optiune necunoscuta: {args[i]}");
     }
}

walPath ??= Path.Combine("data", name + ".wal");

var log = new Logger(name);
log.Info("broker_starting", result: $"gRPC pe portul {port}");

var save = new MessageSave(walPath, log);
var state = new BrokerState(log, save);

int recovered = state.Recover();
if (recovered > 0)
     log.Info("broker_recovered", result: $"{recovered} mesaje din WAL");

// Pornim dispatcher-ul pe un thread separat (la fel ca inainte)
var dispatcher = new Dispatcher(state, log);
var dispatcherThread = new Thread(() => dispatcher.RunForever()) { IsBackground = true, Name = "dispatcher" };
dispatcherThread.Start();

// ASP.NET Core builder — inlocuieste bucla noastra manuala de Accept()
var builder = WebApplication.CreateBuilder(args);

// Kestrel asculta pe portul configurabil, doar HTTP/2 (necesar pentru gRPC)
builder.WebHost.ConfigureKestrel(options =>
{
     options.ListenAnyIP(port, listenOptions =>
     {
          listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2;
     });
});

builder.Services.AddGrpc();

// Inregistram BrokerState ca singleton, ca serviciul gRPC sa il poata folosi
builder.Services.AddSingleton(state);
builder.Services.AddSingleton(log);

var app = builder.Build();

// Mapam serviciul gRPC — echivalentul lui "Accept() -> new Thread(ClientHandler.Run)"
app.MapGrpcService<Service>();

log.Info("broker_started", result: $"gRPC listening on port {port}");
app.Run();