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


// Conexiunea broker-ului cu un receptor: stream-ul, abonarile, ACK-urile si coada lui proprie de livrare
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

     // SemaphoreSlim in loc de object lock, pentru compatibilitate cu async/await.
     private readonly SemaphoreSlim _deliveryLock = new(1, 1);

     public bool IsConnected { get; private set; } = true;

     public ReceiverConnection(string name, IEnumerable<string> subscriptions, IServerStreamWriter<ServerEvent> stream)
     {
          Name = name;
          Subscriptions = new HashSet<string>(subscriptions);
          _stream = stream;
     }

     // Livrarea este async, nu mai blocheaza fire din thread pool cu .Wait().
     public async Task<bool> DeliverAsync(BrokerMessage message)
     {
          await _deliveryLock.WaitAsync();
          try
          {
               if (!IsConnected)
                    return false;

               try
               {
                    // Trimitem mesajul prin stream-ul gRPC (async, fara .Wait())
                    await _stream.WriteAsync(new ServerEvent { Message = message });
               }
               catch (Exception ex) when (ex is InvalidOperationException || ex is RpcException || ex.InnerException is RpcException)
               {
                    // Receptorul s-a deconectat
                    return false;
               }

               // Asteptam ACK — TryTake cu timeout raman sincrone (blocheaza doar firul curent, dar sunt scurte si nu se pot face async pe BlockingCollection).
               while (Acks.TryTake(out AckResult? ack, Constants.AckTimeout))
               {
                    if (ack.MessageId == message.MessageId)
                         return ack.IsAck;
               }

               return false;
          }
          finally
          {
               _deliveryLock.Release();
          }
     }

     public void MarkDisconnected()
     {
          IsConnected = false;
          Acks.CompleteAdding();
          Outbox.CompleteAdding();
     }
}
