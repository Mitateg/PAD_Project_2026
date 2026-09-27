using System.Net;
using System.Net.Sockets;
using System.Text;
using Pad.Common;
using Xunit;

namespace Pad.Common.Tests;

/// <summary>
/// Testam framing-ul cu un socket real pe loopback: trimitem octetii in bucati
/// si verificam ca LineReader recompune corect liniile.
/// </summary>
public class LineReaderTests
{
    [Fact]
    public void ReadLine_RecompuneLiniileDinBucatiDeOcteti()
    {
        // Un mic "server" pe un port liber ales de sistem (port 0).
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        int port = ((IPEndPoint)listener.LocalEndPoint!).Port;

        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        client.Connect(new IPEndPoint(IPAddress.Loopback, port));
        using Socket server = listener.Accept();

        // Trimitem doua mesaje, dar taiate in bucati "nefericite", exact cum poate face TCP.
        client.Send(Encoding.UTF8.GetBytes("{\"a\":1"));
        client.Send(Encoding.UTF8.GetBytes("}\n{\"b\""));
        client.Send(Encoding.UTF8.GetBytes(":2}\r\n"));

        var reader = new LineReader(server);

        Assert.Equal("{\"a\":1}", reader.ReadLine());
        Assert.Equal("{\"b\":2}", reader.ReadLine()); // \r a fost eliminat

        client.Shutdown(SocketShutdown.Send);
        Assert.Null(reader.ReadLine()); // conexiune inchisa
    }
}
