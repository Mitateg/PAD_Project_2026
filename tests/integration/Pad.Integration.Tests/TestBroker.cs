using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pad.Broker;
using Pad.Common;

namespace Pad.Integration.Tests;

/// <summary>
/// Un broker adevarat, pornit in interiorul testului pe un port liber.
///
/// Este acelasi cod ca in src/broker/Program.cs (BrokerState + Dispatcher + Service peste Kestrel),
/// doar ca portul si fisierul WAL se aleg per test, ca testele sa nu se incurce intre ele.
/// </summary>
public sealed class TestBroker : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly MessageSave _save;

    public int Port { get; }
    public string Address => $"http://localhost:{Port}";

    /// <summary>Cate mesaje au fost recuperate din WAL la pornire (pentru testul de repornire).</summary>
    public int RecoveredCount { get; }

    private TestBroker(WebApplication app, MessageSave save, int port, int recovered)
    {
        _app = app;
        _save = save;
        Port = port;
        RecoveredCount = recovered;
    }

    /// <param name="startDispatcher">
    /// false = brokerul doar primeste si salveaza mesaje, fara sa le livreze.
    /// Folosit in testul de WAL, ca sa oprim brokerul cu mesajele inca nelivrate.
    /// </param>
    public static async Task<TestBroker> StartAsync(string walPath, string name, bool startDispatcher = true)
    {
        int port = FindFreePort();

        var log = new Logger(name, toConsole: false);
        var save = new MessageSave(walPath, log);
        var state = new BrokerState(log, save);

        int recovered = state.Recover();

        if (startDispatcher)
        {
            var dispatcher = new Dispatcher(state, log);
            var thread = new Thread(() => dispatcher.RunForever())
            {
                IsBackground = true,
                Name = $"dispatcher-{name}",
            };
            thread.Start();
        }

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();            // fara zgomot ASP.NET in iesirea testelor
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ListenLocalhost(port, listen => listen.Protocols = HttpProtocols.Http2);
        });

        builder.Services.AddGrpc();
        builder.Services.AddSingleton(state);
        builder.Services.AddSingleton(log);

        var app = builder.Build();
        app.MapGrpcService<Service>();

        await app.StartAsync();

        return new TestBroker(app, save, port, recovered);
    }

    /// <summary>Un port liber ales de sistem, ca testele sa poata rula in paralel.</summary>
    private static int FindFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();

        // Important: eliberam fisierul WAL, ca brokerul urmator sa-l poata deschide.
        _save.Dispose();
    }
}
