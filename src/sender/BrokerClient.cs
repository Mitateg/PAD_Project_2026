using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Pad.Common;

namespace Pad.Sender;

/// <summary>
/// Conexiunea sender-ului catre broker, peste un Socket TCP creat manual.
///
/// Fluxul: Connect -> HELLO -> (Message -> asteapta ACK) x N -> Close.
/// Un mesaj este considerat livrat DOAR dupa ce broker-ul raspunde ACK.
/// </summary>
public class BrokerClient : IDisposable
{
    private readonly string _host;
    private readonly int _port;
    private readonly string _name;
    private readonly Logger _log;

    private Socket? _socket;
    private LineReader? _reader;

    /// <summary>Adresa broker-ului, doar pentru mesaje si loguri.</summary>
    private string Address => $"{_host}:{_port}";

    public BrokerClient(string host, int port, string name, Logger log)
    {
        // Pastram host-ul ca text: poate fi IP ("127.0.0.1") sau nume ("broker", in Docker).
        _host = host;
        _port = port;
        _name = name;
        _log = log;
    }

    /// <summary>Creeaza socketul, se conecteaza si trimite HELLO.</summary>
    public void Connect()
    {
        // 1. Cream NOI socketul: IPv4, flux de octeti, TCP.
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

        // Daca broker-ul nu raspunde in timpul asta, Receive() arunca SocketException(TimedOut).
        _socket.ReceiveTimeout = (int)Constants.AckTimeout.TotalMilliseconds;

        // 2. Ne conectam la broker. Aceasta forma de Connect accepta si un nume de host
        //    (il rezolva prin DNS), nu doar un IP: in Docker broker-ul se numeste "broker".
        _socket.Connect(_host, _port);
        _reader = new LineReader(_socket);

        // 3. Primul mesaj este obligatoriu HELLO: ii spunem broker-ului ca suntem sender.
        var hello = new HelloMessage { Role = Constants.RoleSender, Name = _name };
        LineReader.WriteJson(_socket, hello);

        _log.Info("connected", result: $"ok {Address}");
    }

    /// <summary>
    /// Ca Connect(), dar daca broker-ul nu ruleaza inca, asteapta si reincearca la 2 secunde
    /// pana reuseste. Asa poti porni sender-ul inaintea broker-ului.
    /// </summary>
    public void ConnectWithWait()
    {
        while (true)
        {
            try
            {
                Connect();
                return;
            }
            catch (SocketException)
            {
                Console.WriteLine($"Broker-ul nu raspunde pe {Address}. Reincerc in 2 secunde... (Ctrl+C pentru a renunta)");
                Dispose();
                Thread.Sleep(Constants.RetryDelay);
            }
        }
    }

    /// <summary>
    /// Trimite mesajul si asteapta ACK. La timeout sau conexiune pierduta reincearca
    /// (maxim Constants.MaxDeliveryAttempts), cu pauza intre incercari.
    /// Returneaza true daca a primit ACK.
    /// </summary>
    public bool SendWithRetry(Message message)
    {
        string line = Json.Serialize(message);

        for (int attempt = 1; attempt <= Constants.MaxDeliveryAttempts; attempt++)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                BrokerReply reply = SendAndWaitReply(line, message.MessageId);

                if (reply.IsAck)
                {
                    _log.Info("message_acked", message.CorrelationId, message.MessageId, message.MessageType,
                              "ok", stopwatch.Elapsed);
                    return true;
                }

                // NACK = broker-ul a primit mesajul, dar l-a respins (JSON invalid, camp lipsa, tip necunoscut).
                // Acelasi mesaj ar fi respins din nou, deci NU are sens sa reincercam.
                _log.Warn("message_nacked", message.CorrelationId, message.MessageId, message.MessageType,
                          $"nack reason={reply.Reason}", stopwatch.Elapsed);
                return false;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
            {
                _log.Warn("ack_timeout", message.CorrelationId, message.MessageId, message.MessageType,
                          $"attempt={attempt}/{Constants.MaxDeliveryAttempts}", stopwatch.Elapsed);
            }
            catch (Exception ex) when (ex is SocketException || ex is IOException)
            {
                // Conexiunea a picat: o refacem (inclusiv HELLO) si reincercam.
                _log.Warn("connection_lost", message.CorrelationId, message.MessageId, message.MessageType,
                          $"attempt={attempt}/{Constants.MaxDeliveryAttempts} {ex.Message}", stopwatch.Elapsed);
                TryReconnect();
            }

            if (attempt < Constants.MaxDeliveryAttempts)
                Thread.Sleep(Constants.RetryDelay);
        }

        _log.Error("message_failed", message.CorrelationId, message.MessageId, message.MessageType,
                   "no ack after max attempts");
        return false;
    }

    /// <summary>Intreaba broker-ul ce receiveri sunt conectati acum.</summary>
    public List<string> ListReceivers()
    {
        if (_socket is null || _reader is null)
            throw new InvalidOperationException("Nu suntem conectati. Apeleaza Connect() intai.");

        LineReader.WriteJson(_socket, new ControlMessage { Action = ControlMessage.ListReceivers });
        string? replyLine = _reader.ReadLine() ?? throw new IOException("broker-ul a inchis conexiunea");
        return Json.TryDeserialize<ReceiverListReply>(replyLine)?.Receivers ?? new List<string>();
    }

    /// <summary>
    /// Trimite o linie oarecare (chiar si invalida) si asteapta raspunsul broker-ului.
    /// Folosit pentru demo-ul de NACK.
    /// </summary>
    public BrokerReply SendRaw(string line)
    {
        return SendAndWaitReply(line, messageId: null);
    }

    private BrokerReply SendAndWaitReply(string line, string? messageId)
    {
        if (_socket is null || _reader is null)
            throw new InvalidOperationException("Nu suntem conectati. Apeleaza Connect() intai.");

        LineReader.WriteLine(_socket, line);

        string? replyLine = _reader.ReadLine();
        if (replyLine is null)
            throw new IOException("broker-ul a inchis conexiunea");

        BrokerReply? reply = Json.TryDeserialize<BrokerReply>(replyLine);
        if (reply is null)
            throw new IOException($"raspuns neasteptat de la broker: {replyLine}");

        // Verificam ca ACK-ul este chiar pentru mesajul nostru, nu unul ratacit.
        if (messageId is not null && reply.MessageId is not null && reply.MessageId != messageId)
            throw new IOException($"ACK pentru alt mesaj: asteptam {messageId}, am primit {reply.MessageId}");

        return reply;
    }

    private void TryReconnect()
    {
        Dispose();
        try
        {
            Connect();
        }
        catch (SocketException ex)
        {
            _log.Warn("reconnect_failed", result: ex.Message);
        }
    }

    public void Dispose()
    {
        if (_socket is null)
            return;

        try { _socket.Shutdown(SocketShutdown.Both); } catch (SocketException) { /* deja inchis */ }
        _socket.Close();
        _socket = null;
        _reader = null;
    }
}
