using System.Diagnostics;
using Grpc.Core;
using Grpc.Net.Client;
using Pad.Common;
using Pad.Common.Grpc;

namespace Pad.Sender;

/// <summary>
/// Conexiunea sender-ului catre broker, peste gRPC (Partea 2).
///
/// Cele trei metode vin din Protos/broker.proto:
///   RegisterSender  -> ne inregistram si primim numele unic ("sender-1")
///   SendMessage     -> trimitem un mesaj si primim ACK/NACK
///   GetReceivers    -> aflam ce receiveri sunt conectati
///
/// LOAD BALANCING: primim o LISTA de adrese de brokere. Lucram cu unul singur la un moment dat,
/// iar cand acela raspunde UNAVAILABLE (e oprit sau a picat) trecem la urmatorul din lista
/// si ne reinregistram acolo. Acelasi comportament il are si receiver-ul.
///
/// Ce ramane din Partea 1: un mesaj conteaza livrat DOAR dupa ACK, reincercam de
/// Constants.MaxDeliveryAttempts ori, iar un NACK nu se reincearca niciodata.
/// </summary>
public class BrokerClient : IDisposable
{
    private readonly IReadOnlyList<string> _addresses;
    private readonly string _baseName;
    private readonly Func<string, Logger> _createLogger;

    // Se creeaza la prima conectare, dupa ce aflam numele primit de la broker.
    private Logger _log = null!;

    // Pe care broker din lista lucram acum.
    private int _addressIndex;

    private GrpcChannel? _channel;
    private BrokerService.BrokerServiceClient? _client;

    /// <summary>Numele dat de broker-ul curent (ex. "sender-1").</summary>
    public string Name { get; private set; }

    /// <summary>Adresa broker-ului pe care lucram acum (pentru mesaje si loguri).</summary>
    public string CurrentAddress => _addresses[_addressIndex];

    /// <summary>true daca avem mai multe brokere in lista, deci are sens sa vorbim de failover.</summary>
    public bool HasFailover => _addresses.Count > 1;

    public BrokerClient(IReadOnlyList<string> addresses, string baseName, Func<string, Logger> createLogger)
    {
        if (addresses.Count == 0)
            throw new ArgumentException("Trebuie cel putin o adresa de broker.", nameof(addresses));

        _addresses = addresses;
        _baseName = baseName;
        _createLogger = createLogger;
        Name = baseName;
    }

    /// <summary>Deschide canalul gRPC catre broker-ul curent si se inregistreaza la el.</summary>
    public void Connect()
    {
        // Canalul nu deschide inca nicio conexiune: o face la primul apel.
        _channel ??= GrpcChannel.ForAddress(CurrentAddress);
        _client ??= new BrokerService.BrokerServiceClient(_channel);

        // Daca broker-ul nu ruleaza, aici primim RpcException(Unavailable).
        RegisterReply reply = _client.RegisterSender(
            new RegisterSenderRequest { Name = _baseName },
            deadline: DateTime.UtcNow.Add(Constants.AckTimeout));

        Name = string.IsNullOrEmpty(reply.AssignedName) ? _baseName : reply.AssignedName;

        // Logul poarta primul nume primit si nu se schimba la failover,
        // ca sa ramana totul intr-un singur fisier.
        _log ??= _createLogger(Name);
        _log.Info("connected", result: $"ok {CurrentAddress} ca {Name}");
    }

    /// <summary>
    /// Trece la urmatorul broker din lista si deschide un canal nou catre el.
    /// Nu se inregistreaza aici: o face apelantul, cand are nevoie.
    /// </summary>
    private void SwitchToNextBroker()
    {
        string previous = CurrentAddress;

        _channel?.Dispose();
        _channel = null;
        _client = null;

        _addressIndex = (_addressIndex + 1) % _addresses.Count;

        if (HasFailover)
            _log.Warn("broker_switch", result: $"{previous} indisponibil, trec pe {CurrentAddress}");
    }

    /// <summary>
    /// Se conecteaza la primul broker care raspunde. Daca niciunul nu e pornit, le incearca
    /// pe rand la nesfarsit, cu pauza dupa fiecare tur complet (Ctrl+C ca sa renunti).
    /// Asa poti porni sender-ul inaintea brokerelor.
    /// </summary>
    public void ConnectWithWait()
    {
        int failures = 0;

        while (true)
        {
            try
            {
                Connect();
                return;
            }
            catch (RpcException)
            {
                string failed = CurrentAddress;
                failures++;

                // Am incercat toate adresele o data? Atunci asteptam inainte de urmatorul tur.
                bool cycleDone = failures % _addresses.Count == 0;

                // Mutam indexul inainte sa afisam, ca sa putem spune cine urmeaza.
                _channel?.Dispose();
                _channel = null;
                _client = null;
                _addressIndex = (_addressIndex + 1) % _addresses.Count;

                Console.WriteLine(cycleDone
                    ? $"Niciun broker nu raspunde. Reincerc in {Constants.RetryDelay.TotalSeconds}s... (Ctrl+C pentru a renunta)"
                    : $"Broker-ul {failed} nu raspunde, incerc {CurrentAddress}...");

                if (cycleDone)
                    Thread.Sleep(Constants.RetryDelay);
            }
        }
    }

    /// <summary>
    /// Trimite mesajul si asteapta ACK. La timeout reincearca pe acelasi broker;
    /// daca broker-ul e indisponibil, trece pe urmatorul din lista si reincearca acolo.
    /// Maxim Constants.MaxDeliveryAttempts incercari. Returneaza true daca a primit ACK.
    /// </summary>
    public bool SendWithRetry(BrokerMessage message)
    {
        for (int attempt = 1; attempt <= Constants.MaxDeliveryAttempts; attempt++)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                // Acelasi obiect (deci acelasi messageId) la fiecare incercare:
                // daca ACK-ul s-a pierdut dar broker-ul a primit mesajul, receiver-ul
                // vede un duplicat si il arunca. Asta este livrarea "at-least-once".
                SendReply reply = _client!.SendMessage(
                    message,
                    deadline: DateTime.UtcNow.Add(Constants.AckTimeout));

                if (reply.Status == Constants.StatusAck)
                {
                    _log.Info("message_acked", message.CorrelationId, message.MessageId, message.MessageType,
                              $"ok via {CurrentAddress}", stopwatch.Elapsed);
                    return true;
                }

                // NACK = broker-ul a primit mesajul, dar l-a respins (camp lipsa, tip necunoscut).
                // Acelasi mesaj ar fi respins si de alt broker, deci NU reincercam si NU schimbam brokerul.
                _log.Warn("message_nacked", message.CorrelationId, message.MessageId, message.MessageType,
                          $"nack reason={reply.Reason}", stopwatch.Elapsed);
                return false;
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.DeadlineExceeded)
            {
                // Broker-ul traieste, dar nu a raspuns la timp: reincercam la acelasi.
                _log.Warn("ack_timeout", message.CorrelationId, message.MessageId, message.MessageType,
                          $"attempt={attempt}/{Constants.MaxDeliveryAttempts} {CurrentAddress}", stopwatch.Elapsed);
            }
            catch (RpcException ex)
            {
                // Unavailable = broker-ul e oprit. Aici intervine load balancing-ul:
                // trecem pe urmatorul broker si incercam sa ne inregistram la el.
                _log.Warn("connection_lost", message.CorrelationId, message.MessageId, message.MessageType,
                          $"attempt={attempt}/{Constants.MaxDeliveryAttempts} {CurrentAddress} {ex.StatusCode}",
                          stopwatch.Elapsed);

                SwitchToNextBroker();
                TryRegister();
            }

            if (attempt < Constants.MaxDeliveryAttempts)
                Thread.Sleep(Constants.RetryDelay);
        }

        _log.Error("message_failed", message.CorrelationId, message.MessageId, message.MessageType,
                   "no ack after max attempts");
        return false;
    }

    /// <summary>Incearca sa se inregistreze la broker-ul curent, fara sa arunce daca nici el nu raspunde.</summary>
    private void TryRegister()
    {
        try
        {
            Connect();
        }
        catch (RpcException ex)
        {
            _log.Warn("register_failed", result: $"{CurrentAddress}: {ex.StatusCode}");
        }
    }

    /// <summary>
    /// Intreaba broker-ul curent ce receiveri sunt conectati acum.
    /// Daca el a picat, trece pe urmatorul si mai incearca o data; daca nici acolo nu merge,
    /// returneaza lista goala in loc sa arunce: aplicatia nu trebuie sa cada doar
    /// pentru ca a intrebat cine e conectat.
    /// </summary>
    public List<string> ListReceivers()
    {
        for (int attempt = 1; attempt <= 2; attempt++)
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
                _log.Warn("list_receivers_failed", result: $"{CurrentAddress}: {ex.StatusCode}");

                if (attempt == 1 && HasFailover)
                {
                    SwitchToNextBroker();
                    TryRegister();
                }
                else
                {
                    return new List<string>();
                }
            }
        }

        return new List<string>();
    }

    public void Dispose()
    {
        _channel?.Dispose();
        _channel = null;
        _client = null;
    }
}
