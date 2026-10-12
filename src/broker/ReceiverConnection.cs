using System.Collections.Concurrent;
using System.Collections.Concurrent;
using Pad.Common;
using Pad.Common.Grpc;
using Grpc.Core;

namespace Pad.Broker;

/// Rezultatul unui ACK primit de la receiver (inlocuieste BrokerReply in contextul receiver-ului).
public class AckResult
{
     public string MessageId { get; set; } = "";
     public bool IsAck { get; set; }
}


// Conexiunea broker-ului cu un receptor: socketul, abonarile, ACK-urile si coada lui proprie de livrare
public class ReceiverConnection
{
     public string Name { get; }
     public HashSet<string> Subscriptions { get; }

     /// Stream-ul gRPC catre receiver (inlocuieste Socket-ul).
     private readonly IServerStreamWriter<ServerEvent> _stream;

     /// ACK-urile venite de la receiver.
     public BlockingCollection<AckResult> Acks { get; } = new();

     /// Mesajele de livrat catre ACEST receptor.
     public BlockingCollection<QueuedMessage> Outbox { get; } = new();

     // Lock pentru a livra un singur mesaj odata la un receptor.
     public readonly object _deliveryLock = new();

     public bool IsConnected { get; private set; } = true;

     public ReceiverConnection(string name, IEnumerable<string> subscriptions, IServerStreamWriter<ServerEvent> stream)
     {
          Name = name;
          Subscriptions = new HashSet<string>(subscriptions);
          _stream = stream;
     }

     // Livrarea unui mesaj catre receptor prin gRPC stream si asteptarea ACK-ului.
     public bool Deliver(BrokerMessage message)
     {
          lock (_deliveryLock)
          {
               if (!IsConnected)
                    return false;

               try
               {
                    // Trimitem mesajul prin stream-ul gRPC (sincron, cu .Wait())
                    _stream.WriteAsync(new ServerEvent { Message = message }).Wait();
               }
               catch (Exception ex) when (ex is InvalidOperationException || ex is RpcException || ex.InnerException is RpcException)
               {
                    // Receptorul s-a deconectat
                    return false;
               }

               // Asteptam ACK, la fel ca inainte
               while (Acks.TryTake(out AckResult? ack, Constants.AckTimeout))
               {
                    if (ack.MessageId == message.MessageId)
                         return ack.IsAck;
               }

               return false;
          }
     }

     public void MarkDisconnected()
     {
          IsConnected = false;
          Acks.CompleteAdding();
          Outbox.CompleteAdding();
     }
}
