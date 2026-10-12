using System.Diagnostics;
using Grpc.Core;
using Pad.Common;
using Pad.Common.Grpc;

namespace Pad.Broker;

// Inlocuieste ClientHandler.cs.
// Fiecare RPC corespunde unui tip de interactiune care inainte era un mesaj JSON pe socket.
public class Service : BrokerService.BrokerServiceBase
{
     private readonly BrokerState _state;
     private readonly Logger _log;

     public Service(BrokerState state, Logger log)
     {
          _state = state;
          _log = log;
     }

     // senderul se inregistreaza (inlocuieste HELLO)
     public override Task<RegisterReply> RegisterSender(RegisterSenderRequest request, ServerCallContext context)
     {
          string assignedName = _state.RegisterSender(request.Name);
          _log.Info("sender_connected", result: assignedName);

          return Task.FromResult(new RegisterReply { AssignedName = assignedName });
     }

     // Senderul trimite un mesaj
     public override Task<SendReply> SendMessage(BrokerMessage request, ServerCallContext context)
     {
          var stopwatch = Stopwatch.StartNew();

          string? reason = MessageValidator.Validate(request, _state.KnownReceiverNames());

          if (reason is not null)
          {
               _log.Warn("message_rejected", request.CorrelationId, request.MessageId, request.MessageType,
                         $"nack reason={reason}", stopwatch.Elapsed);
               return Task.FromResult(new SendReply
               {
                    Status = Constants.StatusNack,
                    MessageId = request.MessageId,
                    Reason = reason
               });
          }

          _state.Enqueue(request);
          _log.Info("message_received", request.CorrelationId, request.MessageId, request.MessageType,
                    "ok", stopwatch.Elapsed);

          return Task.FromResult(new SendReply
          {
               Status = Constants.StatusAck,
               MessageId = request.MessageId
          });
     }

     // Senderul listeaza receiverii conectati
     public override Task<ListReceiverReply> GetReceivers(ListReceiversRequest request, ServerCallContext context)
     {
          var reply = new ListReceiverReply();
          reply.Receivers.AddRange(_state.ConnectedReceiverNames());
          return Task.FromResult(reply);
     }

     // Inlocuieste HELLO receiver + bucla de citire linii + WriteJson catre receiver
     public override async Task Subscribe(
         IAsyncStreamReader<ReceiverEvent> requestStream,
         IServerStreamWriter<ServerEvent> responseStream,
         ServerCallContext context)
     {
          // Primul mesaj trebuie sa fie RegisterReceiverRequest
          if (!await requestStream.MoveNext(context.CancellationToken))
               return;

          var firstEvent = requestStream.Current;
          if (firstEvent.EventCase != ReceiverEvent.EventOneofCase.Register)
          {
               _log.Warn("invalid_hello", result: "primul mesaj nu este RegisterReceiverRequest");
               return;
          }

          var registerReq = firstEvent.Register;

          // Inregistram receiver-ul
          var receiver = _state.RegisterReceiver(
              registerReq.Name,
              registerReq.SubscribeTo,
              responseStream);

          // Trimitem inapoi numele atribuit
          await responseStream.WriteAsync(new ServerEvent
          {
               Registered = new RegisterReply { AssignedName = receiver.Name }
          }, context.CancellationToken);

          _log.Info("receiver_connected", result: $"{receiver.Name} subscribed to [{string.Join(",", registerReq.SubscribeTo)}]");

          try
          {
               while (await requestStream.MoveNext(context.CancellationToken))
               {
                    var evt = requestStream.Current;
                    switch (evt.EventCase)
                    {
                         case ReceiverEvent.EventOneofCase.Ack:
                              receiver.Acks.Add(new AckResult
                              {
                                   MessageId = evt.Ack.MessageId,
                                   IsAck = evt.Ack.Status == Constants.StatusAck
                              });
                              break;

                         case ReceiverEvent.EventOneofCase.Subscription:
                              _state.ChangeSubscriptions(receiver, evt.Subscription.Action, evt.Subscription.Type);
                              break;
                    }
               }
          }
          finally
          {
               receiver.MarkDisconnected();
               _state.RemoveReceiver(receiver);
               _log.Info("receiver_disconnected", result: receiver.Name);
          }
     }
}
