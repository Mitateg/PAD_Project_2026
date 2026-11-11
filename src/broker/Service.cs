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
     // Enqueue returneaza bool: daca e duplicat, tot dam ACK dar nu re-adaugam.
     public override Task<SendReply> SendMessage(BrokerMessage request, ServerCallContext context)
     {
          var stopwatch = Stopwatch.StartNew();

          string? reason = MessageValidator.Validate(request, _state.KnownReceiverNames());

          if (reason is not null)
          {
               _log.Warn("message_rejected", request.CorrelationId, request.MessageId, request.MessageType, $"nack reason={reason}", stopwatch.Elapsed);
               return Task.FromResult(new SendReply
               {
                    Status = Constants.StatusNack,
                    MessageId = request.MessageId,
                    Reason = reason
               });
          }

          bool isNew = _state.Enqueue(request);
          _log.Info("message_received", request.CorrelationId, request.MessageId, request.MessageType, isNew ? "ok" : "duplicate_ack", stopwatch.Elapsed);

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

     // Trimitem Registered inainte de a inregistra receiver-ul in dispatcher.
     // Altfel, dispatcher-ul poate trimite un mesaj pe stream simultan cu Registered, iar doua WriteAsync concurente pe acelasi stream nu sunt permise.
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

          // Mai intai trimitem Registered, apoi inregistram receiver-ul.
          // RegisterReceiver returneaza numele atribuit, dar trebuie sa-l stim inainte de WriteAsync.
          // Deci separam: generam numele, trimitem reply, apoi inregistram cu stream-ul.
          string assignedName = _state.GenerateReceiverName(registerReq.Name);

          await responseStream.WriteAsync(new ServerEvent
          {
               Registered = new RegisterReply { AssignedName = assignedName }
          }, context.CancellationToken);

          // Acum inregistram, dupa ce Registered a fost trimis pe stream.
          var receiver = _state.RegisterReceiverWithName(
              assignedName,
              registerReq.SubscribeTo,
              responseStream);

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
