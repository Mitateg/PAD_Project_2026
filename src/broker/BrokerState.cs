using System.Collections.Concurrent;
using Grpc.Core;
using Pad.Common;
using Pad.Common.Grpc;

namespace Pad.Broker;

// Mesajul din coada si de cate ori a fost incercat sa fie livrat
public class QueuedMessage
{
     public required BrokerMessage Message { get; set; }
     public DateTime EnqueuedAt { get; set; } = DateTime.UtcNow;

     private readonly object _lock = new();
     private readonly HashSet<string> _acked = new();
     private readonly HashSet<string> _gaveUp = new();
     private readonly HashSet<string> _inProcess = new();
     private readonly Dictionary<string, int> _failures = new();

     public void RestoreAck(string receiver) { lock (_lock) _acked.Add(receiver); }

     public bool TryStartDelivery(string receiver)
     {
          lock (_lock)
          {
               if (_acked.Contains(receiver) || _gaveUp.Contains(receiver))
                    return false;
               return _inProcess.Add(receiver);
          }
     }

     public void Ack(string receiver) { lock (_lock) { _inProcess.Remove(receiver); _acked.Add(receiver); } }
     public void Release(string receiver) { lock (_lock) _inProcess.Remove(receiver); }

     public int Failures(string receiver)
     {
          lock (_lock)
          {
               _failures[receiver] = _failures.GetValueOrDefault(receiver) + 1;
               return _failures[receiver];
          }
     }

     public void GiveUp(string receiver) { lock (_lock) { _inProcess.Remove(receiver); _gaveUp.Add(receiver); } }

     public (int acked, int gaveUp, int inProcess) GetStatus()
     {
          lock (_lock) return (_acked.Count, _gaveUp.Count, _inProcess.Count);
     }
}


public class BrokerState
{
     private readonly Logger _log;
     private readonly MessageSave _save;
     private readonly List<ReceiverConnection> _receivers = new();
     private readonly object _receiversLock = new();
     private readonly string _deadLetterPath;
     private readonly HashSet<string> _knownNames = new();
     private readonly HashSet<string> _senderNames = new();
     private readonly object _senderLock = new();

     public ConcurrentDictionary<string, ConcurrentQueue<QueuedMessage>> Queues = new();

     public BrokerState(Logger log, MessageSave save)
     {
          _log = log;
          _save = save;
          Directory.CreateDirectory("logs");
          _deadLetterPath = Path.Combine("logs", "deadletter.log");
     }

     // Enqueue returneaza true daca mesajul a fost adaugat, false daca era duplicat (deja in WAL).
     // Senderul retrimite acelasi messageId dupa un timeout; nu il mai adaugam a doua oara.
     public bool Enqueue(BrokerMessage message)
     {
          if (_save.ContainsMessage(message.MessageId))
          {
               _log.Info("duplicate_ignored", message.CorrelationId, message.MessageId, message.MessageType,
                         "already in WAL, returning ACK without re-enqueue");
               return false; // duplicat, returnam ACK fara sa adaugam
          }

          _save.Enqueued(message);
          Requeue(new QueuedMessage { Message = message });
          return true;
     }

     public void Requeue(QueuedMessage queued)
     {
          var queue = Queues.GetOrAdd(queued.Message.MessageType, _ => new ConcurrentQueue<QueuedMessage>());
          queue.Enqueue(queued);
     }

     public int Recover()
     {
          List<QueuedMessage> messages = _save.Recover();
          foreach (QueuedMessage q in messages) Requeue(q);
          return messages.Count;
     }

     // RecordAck primeste messageId si receiverName (nu messageType).
     public void RecordAck(string messageId, string receiverName) => _save.Acked(messageId, receiverName);
     public void Complete(QueuedMessage queued) => _save.Done(queued.Message.MessageId);

     // Separam generarea numelui de inregistrare, ca sa putem trimite Registered pe stream inainte de a adauga receiver-ul in lista (evitam race condition pe stream).
     public string GenerateReceiverName(string baseName)
     {
          lock (_receiversLock)
          {
               string assigned;
               for (int i = 1; ; i++)
               {
                    assigned = $"{baseName}-{i}";
                    if (_receivers.All(r => r.Name != assigned))
                         break;
               }
               _knownNames.Add(assigned);
               return assigned;
          }
     }

     public ReceiverConnection RegisterReceiverWithName(string assignedName, IEnumerable<string> subscriptions,
                                                 IServerStreamWriter<ServerEvent> stream)
     {
          lock (_receiversLock)
          {
               var allSubscriptions = new List<string>(subscriptions) { assignedName };
               var receiver = new ReceiverConnection(assignedName, allSubscriptions, stream);
               _receivers.Add(receiver);

               _log.Info("receiver_subscribed", result: $"{receiver.Name} -> {string.Join(", ", receiver.Subscriptions)}");
               return receiver;
          }
     }

     public ReceiverConnection RegisterReceiver(string baseName, IEnumerable<string> subscriptions,
                                                 IServerStreamWriter<ServerEvent> stream)
     {
          string assignedName = GenerateReceiverName(baseName);
          return RegisterReceiverWithName(assignedName, subscriptions, stream);
     }

     public string RegisterSender(string baseName)
     {
          lock (_senderLock)
          {
               string assigned;
               for (int i = 1; ; i++)
               {
                    assigned = $"{baseName}-{i}";
                    if (!_senderNames.Contains(assigned)) break;
               }
               _senderNames.Add(assigned);
               return assigned;
          }
     }

     public void RemoveSender(string assignedName) { lock (_senderLock) _senderNames.Remove(assignedName); }

     public List<string> KnownReceiverNames()
     {
          lock (_receiversLock) return _knownNames.ToList();
     }

     public void RemoveReceiver(ReceiverConnection receiver)
     {
          lock (_receiversLock) _receivers.Remove(receiver);
          _log.Info("receiver_disconnected", result: receiver.Name);
     }

     public void ChangeSubscriptions(ReceiverConnection receiver, string action, string type)
     {
          lock (_receiversLock)
          {
               if (action == "subscribe")
                    receiver.Subscriptions.Add(type);
               else if (type != receiver.Name)
                    receiver.Subscriptions.Remove(type);
          }
          _log.Info("subscription_changed", type: type, result: $"{receiver.Name} {action} -> [{string.Join(", ", receiver.Subscriptions)}]");
     }

     public List<string> ConnectedReceiverNames()
     {
          lock (_receiversLock) return _receivers.Select(r => r.Name).OrderBy(n => n).ToList();
     }

     public List<ReceiverConnection> SubscribersOf(string messageType)
     {
          lock (_receiversLock) return _receivers.Where(r => r.Subscriptions.Contains(messageType)).ToList();
     }

     public void DeadLetter(BrokerMessage message, string reason)
     {
          _log.Error("dead_letter", message.CorrelationId, message.MessageId, message.MessageType, reason);

          string json = System.Text.Json.JsonSerializer.Serialize(new
          {
               message.MessageId,
               message.CorrelationId,
               message.MessageType,
               message.SchemaVersion,
               message.OccurredAt,
               message.PayloadJson
          });
          string line = $"{DateTime.UtcNow:O} | {reason} | {json}";
          lock (_deadLetterPath)
               File.AppendAllText(_deadLetterPath, line + Environment.NewLine);
     }
}
