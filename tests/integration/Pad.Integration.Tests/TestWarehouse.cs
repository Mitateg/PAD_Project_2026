using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pad.Warehouse.Controllers;
using Pad.Warehouse.Data;
using Pad.Warehouse.Services;
using Pad.Warehouse.Startup;

namespace Pad.Integration.Tests;

/// <summary>
/// Un warehouse adevarat (controllere + serviciu + repository in memorie), pornit in interiorul testului
/// pe un port liber. Foloseste aceeasi configurare MVC ca Program.cs (AddWarehouseMvc):
/// JSON + XML, 406 la Accept nesuportat, 400 la cerere invalida.
/// </summary>
public sealed class TestWarehouse : IAsyncDisposable
{
    private readonly WebApplication _app;

    public string Address { get; }

    private TestWarehouse(WebApplication app, string address)
    {
        _app = app;
        Address = address;
    }

    public static async Task<TestWarehouse> StartAsync()
    {
        int port = FindFreePort();

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();   // fara zgomot ASP.NET in iesirea testelor
        builder.WebHost.ConfigureKestrel(options => options.ListenLocalhost(port));

        // Aplicatia "de intrare" este proiectul de test, nu warehouse-ul, deci ii aratam unde sunt controllerele.
        builder.Services.AddWarehouseMvc().AddApplicationPart(typeof(EmployeeController).Assembly);
        builder.Services.AddSingleton<IEmployeeRepository, InMemoryEmployeeRepository>();
        builder.Services.AddSingleton<EmployeeService>();

        WebApplication app = builder.Build();
        app.MapControllers();
        await app.StartAsync();

        return new TestWarehouse(app, $"http://localhost:{port}");
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
    }
}
