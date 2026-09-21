using System.Net.Sockets;
using System.Text;

namespace Pad.Common;

/// <summary>
/// Framing "un mesaj = o linie" peste un Socket TCP.
///
/// TCP este un flux de octeti, nu de mesaje: un Receive() poate aduce jumatate de mesaj
/// sau doua mesaje lipite. Clasa asta aduna octetii intr-un buffer si scoate cate o linie
/// completa (terminata cu newline) la fiecare apel ReadLine().
///
/// Aceeasi clasa este folosita de sender, broker si receiver, ca sa nu scriem framing-ul de 3 ori.
/// </summary>
public class LineReader
{
    private readonly Socket _socket;
    private readonly byte[] _buffer = new byte[4096];
    private readonly StringBuilder _pending = new();

    public LineReader(Socket socket)
    {
        _socket = socket;
    }

    /// <summary>
    /// Blocheaza pana cand avem o linie completa.
    /// Returneaza null daca partea cealalta a inchis conexiunea.
    /// Arunca SocketException daca expira socket.ReceiveTimeout.
    /// </summary>
    public string? ReadLine()
    {
        while (true)
        {
            // 1. Daca avem deja o linie completa in buffer, o returnam.
            string? line = TakeLineFromBuffer();
            if (line is not null)
                return line;

            // 2. Altfel mai citim octeti de pe socket.
            int received = _socket.Receive(_buffer);
            if (received == 0)
                return null; // conexiune inchisa

            _pending.Append(Encoding.UTF8.GetString(_buffer, 0, received));
        }
    }

    private string? TakeLineFromBuffer()
    {
        string text = _pending.ToString();
        int newlineIndex = text.IndexOf('\n');
        if (newlineIndex < 0)
            return null;

        string line = text.Substring(0, newlineIndex).TrimEnd('\r');
        _pending.Remove(0, newlineIndex + 1);
        return line;
    }

    /// <summary>Trimite un text ca o linie (adauga newline la final).</summary>
    public static void WriteLine(Socket socket, string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text + "\n");
        socket.Send(bytes);
    }

    /// <summary>Serializeaza obiectul ca JSON pe o linie si il trimite.</summary>
    public static void WriteJson<T>(Socket socket, T value)
    {
        WriteLine(socket, Json.Serialize(value));
    }
}
