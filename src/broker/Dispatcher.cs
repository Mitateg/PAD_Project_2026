using Pad.Common;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace Pad.Broker;

public class Dispatcher
{
	private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

	private readonly BrokerState _state;
	private readonly Logger _log;

	// Numele tipurilor de mesaje pentru care exista deja un worker activ
	private readonly ConcurrentDictionary<string, byte> _startedWorkers = new();

	public Dispatcher(BrokerState state, Logger log)
	{
		_state = state;
		_log = log;
	}

     // Ruleaza infinit, pornind cate un worker pentru fiecare tip de mesaj care are mesaje in coada
     public void RunForever()
	{
		_log.Info("dispatcher_started");

		while (true)
		{
			foreach (string messageType in _state.Queues.Keys)
			{
				if (_startedWorkers.TryAdd(messageType, 0))
				{
					_log.Info("dispatcher_start_worker", result: messageType);
					_ = Task.Run(() => RunQueueWorker(messageType));
				}
			}

			Thread.Sleep(PollInterval);
		}
	}

     // Workerul care se ocupa de o coada de mesaje, pentru un anumit tip de mesaj
     private void RunQueueWorker(string messageType)
	{
		ConcurrentQueue<QueuedMessage> queue = _state.Queues[messageType];

		while (true)
		{
			bool deliveredSomething = false;
			while (queue.TryPeek(out QueuedMessage? queued))
			{
				List<ReceiverConnection> pending = _state.SubscribersOf(messageType).Where(r => !queued.AckedBy.Contains(r.Name)).ToList();

				if (pending.Count == 0) break; // Nu mai are cine sa primeasca mesajul

				queue.TryDequeue(out _); // Scoate mesajul din coada, ca sa nu fie retrimis de alt worker
				DeliverToAll(queued, pending);
				deliveredSomething = true;
               }

			if (!deliveredSomething) 
				Thread.Sleep(PollInterval);
          }
     }

     // Trimite mesajul catre toti receptorii care nu l-au confirmat inca
     private void DeliverToAll(QueuedMessage queued, List<ReceiverConnection> receivers)
	{
		Message message = queued.Message;
		queued.Attempts++;

		Task<bool>[] deliveries = receivers.Select(receiver => Task.Run(() => DeliverOne(queued, receiver))).ToArray();
		Task.WaitAll(deliveries);
		bool allAcked = deliveries.All(t => t.Result);

		if (allAcked) return;

		if (queued.Attempts >= Constants.MaxDeliveryAttempts)
		{
			_state.DeadLetter(message, "no ack after max attempts");
			return;
          }

		Thread.Sleep(Constants.RetryDelay);
		_state.Enqueue(queued); // Reintoarce mesajul in coada pentru retry
     }

     // Trimite mesajul catre un receptor si asteapta confirmarea
     private bool DeliverOne(QueuedMessage queued, ReceiverConnection receiver)
	{
		Message message = queued.Message;
		var stopwatch = Stopwatch.StartNew();
		bool acked = receiver.Deliver(message);

          if (acked)
		{
               // Marcam ca receptorul a confirmat mesajul, ca sa nu mai fie retrimis
               lock (queued.AckedBy)
				queued.AckedBy.Add(receiver.Name);

			_log.Info("message_delivered", message.CorrelationId, message.MessageId, message.MessageType, $"to={receiver.Name}", stopwatch.Elapsed);

          }
		else
		{
			_log.Warn("delivery_failed", message.CorrelationId, message.MessageId, message.MessageType, $"to={receiver.Name} attempt={queued.Attempts}/{Constants.MaxDeliveryAttempts}", stopwatch.Elapsed);
          }
		return acked;
     }
}