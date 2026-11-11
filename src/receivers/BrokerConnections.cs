using Google.Protobuf;
using Grpc.Net.Client;
using Pad.Common;
using Pad.Common.Grpc;
using BrokerCall = Grpc.Core.AsyncDuplexStreamingCall<Pad.Common.Grpc.ReceiverEvent, Pad.Common.Grpc.ServerEvent>;

namespace Pad.Receiver;

/// <summary>
/// Conexiunea receiver-ului la broker prin gRPC, pe un thread de fundal, separat de meniul din consola.
///
///  - deschide apelul Subscribe (stream in ambele sensuri) si trimite primul eveniment: Register
///  - broker-ul raspunde cu "registered" = numele unic al receiver-ului (ex. "ion-1"), canalul lui privat
///  - primeste mesaje pe stream, le da la MessageProcessor, trimite ACK pe acelasi stream
///  - se reconecteaza singura daca broker-ul cade
///  - meniul apeleaza Subscribe / Unsubscribe, care trimit schimbarile de abonament pe stream
/// </summary>
public class BrokerConnection
{
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(2);

    // Textele pentru SubscriptionChange.Action. TREBUIE sa fie cele pe care le compara
    // BrokerState.ChangeSubscriptions pe broker (altfel o abonare ar fi tratata ca dezabonare).
    // TODO: dupa ce verifici numele lor in Pad.Common.Constants, inlocuieste cu acele constante.
    private const string ActionSubscribe = "subscribe";
    private const string ActionUnsubscribe = "unsubscribe";

    private readonly ReceiverOptions _options;
    private readonly Logger _log;
    private readonly Func<string, MessageProcessor> _createProcessor;

    private readonly HashSet<string> _subscriptions;
    private readonly object _lock = new();

    // Pe un stream gRPC se poate scrie un singur eveniment odata; ACK-urile (thread-ul conexiunii)
    // si schimbarile de abonament (thread-ul meniului) se scriu deci pe rand.
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private readonly CancellationTokenSource _cts = new();
    private readonly ManualResetEventSlim _connectedOnce = new();
    private volatile BrokerCall? _call;
    private MessageProcessor? _processor;
    private volatile bool _stopping;
    private int _addressIndex;     // brokerul curent din lista de adrese
    private int _failuresInARow;   // esecuri consecutive; se reseteaza la o conectare reusita

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
    public void Start() => Task.Run(RunForeverAsync);

    /// <summary>Blocheaza pana la prima conectare reusita (ca meniul sa apara dupa mesajul de conectare).</summary>
    public void WaitForFirstConnection() => _connectedOnce.Wait();

    /// <summary>Opreste reconectarea si inchide apelul catre broker.</summary>
    public void Stop()
    {
        _stopping = true;
        _cts.Cancel();
    }

    public void Subscribe(string type)
    {
        lock (_lock)
        {
            if (!_subscriptions.Add(type))
                return;
        }
        SendSubscriptionChange(ActionSubscribe, type);
        _log.Info("subscribed", type: type);
    }

    public void Unsubscribe(string type)
    {
        lock (_lock)
        {
            if (!_subscriptions.Remove(type))
                return;
        }
        SendSubscriptionChange(ActionUnsubscribe, type);
        _log.Info("unsubscribed", type: type);
    }

    // Trimite comanda doar daca suntem conectati; daca nu, abonarile pleaca oricum cu urmatorul Register.
    private void SendSubscriptionChange(string action, string type)
    {
        BrokerCall? call = _call;
        if (call is null)
            return;

        var change = new ReceiverEvent { Subscription = new SubscriptionChange { Action = action, Type = type } };
        TryWriteAsync(call, change).GetAwaiter().GetResult();
    }

    private async Task<bool> TryWriteAsync(BrokerCall call, ReceiverEvent evt)
    {
        await _writeLock.WaitAsync();
        try
        {
            await call.RequestStream.WriteAsync(evt);
            return true;
        }
        catch (Exception ex) when (ex is Grpc.Core.RpcException || ex is InvalidOperationException || ex is OperationCanceledException)
        {
            return false; // conexiunea a picat; bucla de fundal se reconecteaza
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task RunForeverAsync()
    {
        IReadOnlyList<string> addresses = _options.BrokerAddresses;

        while (!_stopping)
        {
            string address = addresses[_addressIndex];
            try
            {
                await ConnectAndReceiveAsync(address, _cts.Token);
            }
            catch (Exception ex)
            {
                if (_stopping)
                    return; // noi am inchis conexiunea, nu e o eroare
                _log.Warn("connection_error", result: $"{address}: {ex.Message}");
            }

            if (_stopping)
                return;

            _call = null;
            _failuresInARow++;
            _addressIndex = (_addressIndex + 1) % addresses.Count;

            // Cat timp mai avem un broker de incercat in tura asta, trecem la el imediat;
            // pauza vine abia dupa ce le-am incercat pe toate fara succes.
            bool cycleDone = _failuresInARow % addresses.Count == 0;
            _log.Warn("broker_unavailable", result: $"{address}, urmeaza {addresses[_addressIndex]}");
            Console.WriteLine(cycleDone
                ? $"[brokere indisponibile, reincerc in {ReconnectDelay.TotalSeconds}s]"
                : $"[broker {address} indisponibil, incerc {addresses[_addressIndex]}]");

            if (cycleDone)
            {
                try { await Task.Delay(ReconnectDelay, _cts.Token); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private async Task ConnectAndReceiveAsync(string address, CancellationToken token)
    {
        using var channel = GrpcChannel.ForAddress(address);
        var client = new BrokerService.BrokerServiceClient(channel);
        using BrokerCall call = client.Subscribe(cancellationToken: token);

        // Primul eveniment TREBUIE sa fie Register, cu numele dorit si abonarile curente
        // (asa se refac abonarile dupa o reconectare). Luam lock-ul de scriere inainte de a
        // publica _call, ca nicio schimbare de abonament sa nu ajunga inaintea lui Register.
        await _writeLock.WaitAsync(token);
        try
        {
            var register = new RegisterReceiverRequest { Name = _options.Name };
            lock (_lock)
            {
                register.SubscribeTo.AddRange(_subscriptions);
                _call = call;
            }
            await call.RequestStream.WriteAsync(new ReceiverEvent { Register = register });
        }
        finally
        {
            _writeLock.Release();
        }

        // Broker-ul raspunde cu numele unic pe care ni l-a dat.
        if (!await call.ResponseStream.MoveNext(token)
            || call.ResponseStream.Current.EventCase != ServerEvent.EventOneofCase.Registered)
        {
            throw new InvalidOperationException("Raspuns neasteptat de la broker (lipseste confirmarea inregistrarii).");
        }

        AssignedName = call.ResponseStream.Current.Registered.AssignedName;
        _failuresInARow = 0;
        Console.Title = "receiver " + AssignedName;
        MessageProcessor processor = _processor ??= _createProcessor(AssignedName);

        _log.Info("connected", result: $"as {AssignedName}, subscribed to [{string.Join(",", PublicSubscriptions)}]");
        Console.WriteLine($"[conectat la broker] Numele tau este \"{AssignedName}\" (asa te alege sender-ul pentru un mesaj privat).");

        bool isReconnect = _connectedOnce.IsSet;
        _connectedOnce.Set();

        // La reconectare, meniul a ramas mai sus pe ecran: il reafisam noi.
        if (isReconnect)
            processor.AfterMessageShown?.Invoke();

        while (await call.ResponseStream.MoveNext(token))
        {
            ServerEvent evt = call.ResponseStream.Current;
            if (evt.EventCase == ServerEvent.EventOneofCase.Message)
                await HandleMessageAsync(call, processor, evt.Message);
        }
        // Stream-ul s-a inchis (broker oprit): iesim, iar RunForeverAsync reincearca.
    }

    private async Task HandleMessageAsync(BrokerCall call, MessageProcessor processor, BrokerMessage message)
    {
        if (string.IsNullOrEmpty(message.MessageId) || string.IsNullOrEmpty(message.MessageType))
        {
            _log.Warn("invalid_message", result: JsonFormatter.Default.Format(message));
            return;
        }

        _log.Info("message_received", message.CorrelationId, message.MessageId, message.MessageType);
        processor.Process(message, isPrivate: message.MessageType == AssignedName);

        if (_options.CrashBeforeAck)
        {
            // Scenariul critic: am procesat, dar "cadem" inainte sa confirmam.
            _log.Error("crash_before_ack", message.CorrelationId, message.MessageId, message.MessageType, "simulated crash");
            Environment.Exit(1);
        }

        var ack = new ReceiverEvent { Ack = new ReceiverAck { MessageId = message.MessageId, Status = Constants.StatusAck } };
        if (await TryWriteAsync(call, ack))
            _log.Info("ack_sent", message.CorrelationId, message.MessageId, message.MessageType);
        else
            _log.Warn("ack_failed", message.CorrelationId, message.MessageId, message.MessageType, "stream closed");
    }
}