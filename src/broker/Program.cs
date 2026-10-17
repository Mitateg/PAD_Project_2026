using Pad.Common;
using Pad.Broker;

// Punct de intrare a brokerului, care accepta conexiuni de la clienti (senderi si receptori) si porneste un dispatcher pentru a livra mesajele catre receptori
var log = new Logger("broker");
log.Info("broker_starting", result: $"gRPC pe portul {Constants.BrokerPort}");

var save = new MessageSave(Path.Combine("data", "broker.wal"), log);
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

// Kestrel asculta pe portul nostru, doar HTTP/2 (necesar pentru gRPC)
builder.WebHost.ConfigureKestrel(options =>
{
     options.ListenAnyIP(Constants.BrokerPort, listenOptions =>
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

log.Info("broker_started", result: $"gRPC listening on port {Constants.BrokerPort}");
app.Run();
