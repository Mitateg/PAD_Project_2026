using System.Diagnostics;
using Grpc.Core;
using Grpc.Net.Client;
using Pad.Common;
using Pad.Common.Grpc;

namespace Pad.Sender;

/// <summary>
/// Conexiunea sender-ului catre broker, peste gRPC (Partea 2).
///
/// Inainte (Partea 1) aici erau: Socket, LineReader, framing pe linii, serializare JSON
/// scrisa de mana si citirea raspunsului linie cu linie. Toate acelea au disparut.
/// Acum apelam trei metode generate automat din Protos/broker.proto:
///
///   RegisterSender  -> ne inregistram si primim numele unic ("sender-1")   [inlocuieste HELLO]
///   SendMessage     -> trimitem un mesaj si primim ACK/NACK                [inlocuieste WriteJson + ReadLine]
///   GetReceivers    -> aflam ce receiveri sunt conectati                   [inlocuieste list_receivers]
///
/// Ce ramane neschimbat din Partea 1: un mesaj conteaza livrat DOAR dupa ACK,
/// reincercam de Constants.MaxDeliveryAttempts ori, iar un NACK nu se reincearca.
/// </summary>
public class BrokerClient : IDisposable
{
    private readonly string _address;
    private readonly string _baseName;
    private readonly Func<string, Logger> _createLogger;

    // Se creeaza la prima conectare, dupa ce aflam numele primit de la broker.
    private Logger _log = null!;

    // Canalul gRPC tine conexiunea HTTP/2 si se reconecteaza SINGUR daca broker-ul
    // repornește. De-aia nu mai avem nevoie de TryReconnect() scris de mana, ca la socket.
    private GrpcChannel? _channel;
    private BrokerService.BrokerServiceClient? _client;

    /// <summary>Numele dat de broker (ex. "sender-1"). Pana la prima conectare, numele cerut.</summary>
    public string Name { get; private set; }

    public BrokerClient(string address, string baseName, Func<string, Logger> createLogger)
    {
        _address = address;
        _baseName = baseName;
        _createLogger = createLogger;
        Name = baseName;
    }

    /// <summary>Deschide canalul gRPC si se inregistreaza la broker.</summary>
    public void Connect()
    {
        // 1. Canalul nu deschide inca nicio conexiune: o face la primul apel.
        //    Il cream o singura data si il refolosim (asa se recomanda la gRPC).
        _channel ??= GrpcChannel.ForAddress(_address);
        _client ??= new BrokerService.BrokerServiceClient(_channel);

        // 2. Ne inregistram. Daca broker-ul nu ruleaza, aici primim RpcException(Unavailable).
        RegisterReply reply = _client.RegisterSender(
            new RegisterSenderRequest { Name = _baseName },
            deadline: DateTime.UtcNow.Add(Constants.AckTimeout));

        // 3. Broker-ul ne da numele unic: "sender" -> "sender-1", al doilea "sender-2".
        Name = string.IsNullOrEmpty(reply.AssignedName) ? _baseName : reply.AssignedName;

        // Logul poarta numele primit. La o reconectare pastram acelasi fisier de log.
        _log ??= _createLogger(Name);
        _log.Info("connected", result: $"ok {_address} ca {Name}");
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
            catch (RpcException)
            {
                Console.WriteLine($"Broker-ul nu raspunde pe {_address}. Reincerc in 2 secunde... (Ctrl+C pentru a renunta)");
                Thread.Sleep(Constants.RetryDelay);
            }
        }
    }

    /// <summary>
    /// Trimite mesajul si asteapta ACK. La timeout sau broker indisponibil reincearca
    /// (maxim Constants.MaxDeliveryAttempts), cu pauza intre incercari.
    /// Returneaza true daca a primit ACK.
    /// </summary>
    public bool SendWithRetry(BrokerMessage message)
    {
        for (int attempt = 1; attempt <= Constants.MaxDeliveryAttempts; attempt++)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                // Acelasi obiect (deci acelasi messageId) la fiecare incercare:
                // daca ACK-ul s-a pierdut dar broker-ul a primit mesajul, el vede un duplicat
                // si receiver-ul il poate arunca. Asta este livrarea "at-least-once".
                SendReply reply = _client!.SendMessage(
                    message,
                    deadline: DateTime.UtcNow.Add(Constants.AckTimeout));

                if (reply.Status == Constants.StatusAck)
                {
                    _log.Info("message_acked", message.CorrelationId, message.MessageId, message.MessageType,
                              "ok", stopwatch.Elapsed);
                    return true;
                }

                // NACK = broker-ul a primit mesajul, dar l-a respins (camp lipsa, tip necunoscut).
                // Acelasi mesaj ar fi respins din nou, deci NU are sens sa reincercam.
                _log.Warn("message_nacked", message.CorrelationId, message.MessageId, message.MessageType,
                          $"nack reason={reply.Reason}", stopwatch.Elapsed);
                return false;
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.DeadlineExceeded)
            {
                // Nu a raspuns in Constants.AckTimeout. Echivalentul socket-ului care dadea TimedOut.
                _log.Warn("ack_timeout", message.CorrelationId, message.MessageId, message.MessageType,
                          $"attempt={attempt}/{Constants.MaxDeliveryAttempts}", stopwatch.Elapsed);
            }
            catch (RpcException ex)
            {
                // Unavailable = broker-ul e oprit sau a picat conexiunea. Canalul gRPC se reface
                // singur la urmatorul apel, deci doar asteptam si reincercam.
                _log.Warn("connection_lost", message.CorrelationId, message.MessageId, message.MessageType,
                          $"attempt={attempt}/{Constants.MaxDeliveryAttempts} {ex.StatusCode}", stopwatch.Elapsed);
            }

            if (attempt < Constants.MaxDeliveryAttempts)
                Thread.Sleep(Constants.RetryDelay);
        }

        _log.Error("message_failed", message.CorrelationId, message.MessageId, message.MessageType,
                   "no ack after max attempts");
        return false;
    }

    /// <summary>
    /// Intreaba broker-ul ce receiveri sunt conectati acum.
    /// Daca broker-ul a picat intre timp, returneaza lista goala in loc sa arunce:
    /// aplicatia nu trebuie sa cada doar pentru ca a intrebat cine e conectat.
    /// </summary>
    public List<string> ListReceivers()
    {
        try
        {
            ListReceiverReply reply = _client!.GetReceivers(
                new ListReceiversRequest(),
                deadline: DateTime.UtcNow.Add(Constants.AckTimeout));

            return reply.Receivers.ToList();
        }
        catch (RpcException ex)
        {
            _log.Warn("list_receivers_failed", result: ex.StatusCode.ToString());
            return new List<string>();
        }
    }

    public void Dispose()
    {
        _channel?.Dispose();
        _channel = null;
        _client = null;
    }
}
