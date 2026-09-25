using System.Collections.Concurrent;
using Pad.Common;

namespace Pad.Broker;

// Mesajul din coada si de cate ori a fost incercat sa fie livrat
public class QueuedMessage
{
     public required Message Message { get; set; }
     public int Attempts { get; set; }

     // Numele receptorilor ce au confirmat deja, fiind ignorati la retrimitere
     public HashSet<string> AckedBy { get; } = new();
}



public class BrokerState
{
     private readonly Logger _log;
     private readonly List<ReceiverConnection> _receivers = new();
     private readonly object _receiversLock = new();
     private readonly string _deadLetterPath;

     // Toate numele de receptori cunoscuti, inclusiv cei deconectati
     private readonly HashSet<string> _knownNames = new();

     // Cate o coada pentru fiecare tip de mesaj
     public ConcurrentDictionary<string, ConcurrentQueue<QueuedMessage>> Queues = new();

     public BrokerState(Logger log)
     {
          _log = log;
          Directory.CreateDirectory("logs");
          _deadLetterPath = Path.Combine("logs", "deadletter.log");
     }

     public void Enqueue(Message message)
     {
          Enqueue(new QueuedMessage { Message = message });
     }

     public void Enqueue(QueuedMessage queued)
     {
          var queue = Queues.GetOrAdd(queued.Message.MessageType, _ => new ConcurrentQueue<QueuedMessage>());
          queue.Enqueue(queued);
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