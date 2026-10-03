using System.Net;
using System.Net.Sockets;
using Pad.Common;

namespace Pad.Receiver;

/// <summary>
/// Conexiunea receiver-ului la broker, pe un thread separat de meniul din consola.
///
///  - se conecteaza (si se reconecteaza singura daca broker-ul cade), trimite HELLO cu abonarile curente
///  - broker-ul raspunde cu numele unic al acestui receiver (ex. "ion-1") = canalul lui privat
///  - primeste mesaje, le da la MessageProcessor, trimite ACK
///  - meniul apeleaza Subscribe / Unsubscribe, care trimit comenzi broker-ului
/// </summary>
public class BrokerConnection
{
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(2);

    private readonly ReceiverOptions _options;
    private readonly Logger _log;
    private readonly Func<string, MessageProcessor> _createProcessor;

    private readonly HashSet<string> _subscriptions;
    private readonly object _lock = new();
    private Socket? _socket;
    private volatile bool _stopping;
    private readonly ManualResetEventSlim _connectedOnce = new();
    private MessageProcessor? _processor;

    /// <summary>Numele unic primit de la broker. Null pana la prima conectare.</summary>
    public string? AssignedName { get; private set; }

    public BrokerConnection(ReceiverOptions options, Logger log, Func<string, MessageProcessor> createProcessor)
    {
        _options = options;
        _log = log;
        _createProcessor = createProcessor;
        _subscriptions = new HashSet<string>(options.Subscriptions);
    }

    /// <summary>Abonarile publice (fara canalul privat, care e implicit si nu se poate scoate).</summary>
    public IReadOnlyCollection<string> PublicSubscriptions
    {
        get { lock (_lock) return _subscriptions.ToList(); }
    }

    /// <summary>Porneste bucla de conectare + primire pe un thread de fundal.</summary>
    public void Start()
    {
        var thread = new Thread(RunForever) { IsBackground = true, Name = "broker-connection" };
        thread.Start();
    }

    /// <summary>Blocheaza pana la prima conectare reusita (ca meniul sa apara dupa mesajul de conectare).</summary>
    public void WaitForFirstConnection() => _connectedOnce.Wait();

    /// <summary>Inchide frumos conexiunea (Shutdown + Close) si opreste reconectarea.</summary>
    public void Stop()
    {
        _stopping = true;
        lock (_lock)
        {
            if (_socket is null)
                return;
            try { _socket.Shutdown(SocketShutdown.Both); } catch (SocketException) { /* deja inchis */ }
            _socket.Close();
            _socket = null;
        }
    }

    public void Subscribe(string type)
    {
        lock (_lock)
        {
            if (!_subscriptions.Add(type))
                return;
            SendControl(ControlMessage.Subscribe, type);
        }
        _log.Info("subscribed", type: type);
    }

    public void Unsubscribe(string type)
    {
        lock (_lock)
        {
            if (!_subscriptions.Remove(type))
                return;
            SendControl(ControlMessage.Unsubscribe, type);
        }
        _log.Info("unsubscribed", type: type);
    }

    // Trimite comanda doar daca suntem conectati; daca nu, abonarile pleaca oricum cu urmatorul HELLO.
    private void SendControl(string action, string type)
    {
        if (_socket is null)
            return;
        try
        {
            LineReader.WriteJson(_socket, new ControlMessage { Action = action, Type = type });
        }
        catch (SocketException) { /* conexiunea a picat; bucla de fundal se reconecteaza */ }
    }

    private void RunForever()
    {
        while (!_stopping)
        {
            try
            {
                ConnectAndReceive();
            }
            catch (Exception ex) when (ex is SocketException || ex is ObjectDisposedException)
            {
                if (_stopping)
                    return; // noi am inchis socketul, nu e o eroare
                _log.Warn("connection_error", result: ex.Message);
            }

            if (_stopping)
                return;

            lock (_lock) _socket = null;
            _log.Warn("broker_disconnected", result: $"reincerc in {ReconnectDelay.TotalSeconds}s");
            Console.WriteLine($"[broker deconectat, reincerc in {ReconnectDelay.TotalSeconds}s]");
            Thread.Sleep(ReconnectDelay);
        }
    }

    private void ConnectAndReceive()
    {
        // Cream NOI socketul si ne conectam la broker.
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        // Connect(host, port) accepta si un nume de host (DNS), nu doar un IP: in Docker broker-ul se numeste "broker".
        socket.Connect(_options.Host, _options.Port);

        // HELLO cu numele dorit si abonarile curente (asa se refac abonarile dupa o reconectare).
        HelloMessage hello;
        lock (_lock)
        {
            hello = new HelloMessage
            {
                Role = Constants.RoleReceiver,
                Name = _options.Name,
                SubscribeTo = _subscriptions.ToList(),
            };
            _socket = socket;
        }
        LineReader.WriteJson(socket, hello);

        // Broker-ul raspunde cu numele unic pe care ni l-a dat.
        var reader = new LineReader(socket);
        string? helloReply = reader.ReadLine();
        BrokerReply? reply = helloReply is null ? null : Json.TryDeserialize<BrokerReply>(helloReply);
        if (reply?.AssignedName is null)
            throw new SocketException((int)SocketError.ProtocolNotSupported); // broker vechi / raspuns neasteptat

        AssignedName = reply.AssignedName;
        Console.Title = "receiver " + AssignedName;
        _processor ??= _createProcessor(AssignedName);

        _log.Info("connected", result: $"as {AssignedName}, subscribed to [{string.Join(",", hello.SubscribeTo)}]");
        Console.WriteLine($"[conectat la broker] Numele tau este \"{AssignedName}\" (asa te alege sender-ul pentru un mesaj privat).");

        bool isReconnect = _connectedOnce.IsSet;
        _connectedOnce.Set();

        // La reconectare, meniul a ramas mai sus pe ecran: il reafisam noi.
        if (isReconnect)
            _processor.AfterMessageShown?.Invoke();

        while (true)
        {
            string? line = reader.ReadLine();
            if (line is null)
                return; // broker-ul a inchis conexiunea

            Message? message = Json.TryDeserialize<Message>(line);
            if (message is null)
            {
                _log.Warn("invalid_message", result: line);
                continue;
            }

            _log.Info("message_received", message.CorrelationId, message.MessageId, message.MessageType);
            _processor.Process(message, line, isPrivate: message.MessageType == AssignedName);

            if (_options.CrashBeforeAck)
            {
                // Scenariul critic: am procesat, dar "cadem" inainte sa confirmam.
                _log.Error("crash_before_ack", message.CorrelationId, message.MessageId, message.MessageType, "simulated crash");
                Environment.Exit(1);
            }

            LineReader.WriteJson(socket, BrokerReply.Ack(message.MessageId));
            _log.Info("ack_sent", message.CorrelationId, message.MessageId, message.MessageType);
        }
    }
}