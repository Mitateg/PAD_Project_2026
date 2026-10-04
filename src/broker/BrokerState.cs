using System.Collections.Concurrent;
using Pad.Common;

namespace Pad.Broker;

// Mesajul din coada si de cate ori a fost incercat sa fie livrat
public class QueuedMessage
{
     public required Message Message { get; set; }

     // Cand a fost pus in coada, pentru a putea face retry dupa un timp
     public DateTime EnqueuedAt { get; set; } = DateTime.UtcNow;

     private readonly object _lock = new();

     // Numele receptorilor care au confirmat deja, fiind ignorati la retrimitere
     private readonly HashSet<string> _acked = new();

     // Numele receptorilor care au renuntat la mesaj, fiind ignorati la retrimitere
     private readonly HashSet<string> _gaveUp = new();

     // Numele receptorilor care sunt in proces de livrare
     private readonly HashSet<string> _inProcess = new();

     private readonly Dictionary<string, int> _failures = new();

     // La recuperare, se restaureaza lista de receptori care au confirmat deja, ca sa nu mai fie retrimisi
     public void RestoreAck(string receiver)
     {
          lock (_lock)
               _acked.Add(receiver);
     }

     // Daca receptorul a confirmat, il marcam ca acked, si nu mai incercam sa-l trimitem
     public bool TryStartDelivery(string receiver)
     {
          lock (_lock)
          {
               if (_acked.Contains(receiver) || _gaveUp.Contains(receiver))
                    return false;
               return _inProcess.Add(receiver);
          }
     }

     public void Ack(string receiver)
     {
          lock (_lock)
          {
               _inProcess.Remove(receiver);
               _acked.Add(receiver);
          }
     }

     // Livrarea s-a oprit fara verdict (receptorul s-a deconectat); mesajul poate fi timis mai tarziu
     public void Release(string receiver)
     {
          lock (_lock)
               _inProcess.Remove(receiver);
     }

     public int Failures(string receiver)
     {
          lock (_lock)
          {
               _failures[receiver] = _failures.GetValueOrDefault(receiver) + 1;
               return _failures[receiver];
          }
     }

     public void GiveUp(string receiver)
     {
          lock (_lock)
          {
               _inProcess.Remove(receiver);
               _gaveUp.Add(receiver);
          }
     }

     public (int acked, int gaveUp, int inProcess) GetStatus()
     {
          lock (_lock)
               return (_acked.Count, _gaveUp.Count, _inProcess.Count);
     }
}



public class BrokerState
{
     private readonly Logger _log;
     private readonly MessageSave _save;
     private readonly List<ReceiverConnection> _receivers = new();
     private readonly object _receiversLock = new();
     private readonly string _deadLetterPath;

     // Toate numele de receptori cunoscuti, inclusiv cei deconectati
     private readonly HashSet<string> _knownNames = new();

     // Numele senderilor conectati acum
     private readonly HashSet<string> _senderNames = new();
     private readonly object _senderLock = new();

     // Cate o coada pentru fiecare tip de mesaj
     public ConcurrentDictionary<string, ConcurrentQueue<QueuedMessage>> Queues = new();

     public BrokerState(Logger log, MessageSave save)
     {
          _log = log;
          _save = save;
          Directory.CreateDirectory("logs");
          _deadLetterPath = Path.Combine("logs", "deadletter.log");
     }

     // Salvam mesajul in fisier si il punem in coada
     public void Enqueue(Message message)
     {
          _save.Enqueued(message);
          Requeue(new QueuedMessage { Message = message });
     }

     // Punem mesajul inapoi in coada, pentru retry
     public void Requeue(QueuedMessage queued)
     {
          var queue = Queues.GetOrAdd(queued.Message.MessageType, _ => new ConcurrentQueue<QueuedMessage>());
          queue.Enqueue(queued);
     }

     // Recuperam mesajele din fisier si le punem inapoi in coada
     public int Recover()
     {
          List<QueuedMessage> messages = _save.Recover();
          foreach (QueuedMessage q in messages)
               Requeue(q);
          return messages.Count;
     }

     public void RecordAck(string messageId, string receiver)
     {
          _save.Acked(messageId, receiver);
     }

     public void Complete(QueuedMessage queued)
     {
          _save.Done(queued.Message.MessageId);
     }

     // Da un nume unic, si adauga receptorul in lista de receptori, ca sa nu sa se repete numele
     public ReceiverConnection RegisterReceiver(string baseName, IEnumerable<string> subscriptions, System.Net.Sockets.Socket socket)
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

               // Canalul privat este merue in lista de abonari
               var allSubscriptions = new List<string>(subscriptions) { assigned};
               var receiver = new ReceiverConnection(assigned, allSubscriptions, socket);
               _receivers.Add(receiver);

               _log.Info("receiver_subscribed", result: $"{receiver.Name} -> {string.Join(", ", receiver.Subscriptions)}");
               return receiver;
          }
     }

     // Da un nume unic, si adauga senderul in lista de receptori, ca sa nu sa se repete numele
     public string RegisterSender(string baseName)
     {
          lock (_senderLock)
          {
               string assigned;
               for(int i = 1; ; i++)
               {
                    assigned = $"{baseName}-{i}";
                    if (!_senderNames.Contains(assigned))
                         break;
               }
               _senderNames.Add(assigned);
               return assigned;
          }
     }

     public void RemoveSender(string assignedName)
     {
          lock (_senderLock) 
               _senderNames.Remove(assignedName);
     }

     // Returneaza lista de receptori cunoscuti, inclusiv cei deconectati
     public List<string> KnownReceiverNames()
     {
          lock (_receiversLock)
          {
               return _knownNames.ToList();
          }
     }

     public void RemoveReceiver(ReceiverConnection receiver)
     {
          lock (_receiversLock)
               _receivers.Remove(receiver);

          _log.Info("receiver_disconnected", result: $"{receiver.Name}"); 
     }

     // Receptorul schimba tipul de abonament
     public void ChangeSubscriptions(ReceiverConnection receiver, string action, string type)
     {
          lock (_receiversLock)
          {
               if (action == ControlMessage.Subscribe)
                    receiver.Subscriptions.Add(type);
               else if (type != receiver.Name) // Nu permitem dezabonarea de la canalul privat
                    receiver.Subscriptions.Remove(type);
               
          }
          _log.Info("subscription_changed", type: type, result: $"{receiver.Name} {action} -> [{string.Join(", ", receiver.Subscriptions)}]");
     }

     // Numele receptorilor conectati
     public List<string> ConnectedReceiverNames()
     {
          lock (_receiversLock)
               return _receivers.Select(r => r.Name).OrderBy(n => n).ToList();
     }

     // Lista de receptori care sunt abonati la un anumit tip de mesaj
     public List<ReceiverConnection> SubscribersOf(string messageType)
     {
          lock (_receiversLock)
               return _receivers.Where(r => r.Subscriptions.Contains(messageType)).ToList();
     }

     // Mesajul nu a putut fi livrat la niciun receptor, il punem in dead letter
     public void DeadLetter(Message message, string reason)
     {
          _log.Error("dead_letter", message.CorrelationId, message.MessageId, message.MessageType, reason);

          string line = $"{DateTime.UtcNow:O} | {reason} | {Json.Serialize(message)}";
          lock (_deadLetterPath)
               File.AppendAllText(_deadLetterPath, line + Environment.NewLine);
     }

}