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

	// Numele receptorilor pentru care exista deja un loop activ care trimite mesaje din Outbox
	private readonly ConcurrentDictionary<ReceiverConnection, byte> _receiverLoops = new();

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
			int count = queue.Count;
			for (int i = 0; i < count && queue.TryDequeue(out QueuedMessage? queued); i++)
			{
				try
				{
					Examine(queued);
				}
				catch (Exception ex)
				{
					_log.Error("dispatcher_error", queued.Message.CorrelationId, queued.Message.MessageId, queued.Message.MessageType, ex.Message);
					queue.Enqueue(queued);
				}
			}
			Thread.Sleep(PollInterval);
		}
	}

	private void Examine(QueuedMessage queued)
	{
		Message message = queued.Message;
		int dispatched = 0;
		foreach (ReceiverConnection receiver in _state.SubscribersOf(message.MessageType))
		{

			if (!queued.TryStartDelivery(receiver.Name))
				continue;
			Submit(queued, receiver);
			dispatched++;
		}

		var (acked, gaveUp, inProcess) = queued.GetStatus();
		// Inca se lucreaza la el: il tinem in coada si ne uitam din nou data viitoare.
		if (dispatched > 0 || inProcess > 0)
		{
			_state.Requeue(queued);
			return;
		}

		// Nimeni nu mai are nimic de facut cu el. Daca cineva l-a primit, e gata.
		if (acked > 0 || gaveUp > 0)
		{
			_state.Complete(queued);
			_log.Info("message_complete", message.CorrelationId, message.MessageId, message.MessageType, $"acked={acked} gaveUp={gaveUp}");
			return;
		}

		// Nimeni nu l-a primit si nici nu mai poate sa-l primeasca. Il punem in dead-letter dupa ce expira timpul.
		if (DateTime.UtcNow - queued.EnqueuedAt >= Constants.MessageTtl)
		{
			_state.DeadLetter(message, $"expired: no subscriber acked in {Constants.MessageTtl.TotalSeconds} seconds");
			return;
		}

		_state.Requeue(queued);

	}

	private void Submit(QueuedMessage queued, ReceiverConnection receiver)
	{
		EndureReceiverLoop(receiver);
		AddToOutbox(queued, receiver);
	}

	// Pune mesajul in Outbox. Daca receptorul s-a deconectat intre timp, Outbox e inchis, deci eliberam rezervarea.
	private void AddToOutbox(QueuedMessage queued, ReceiverConnection receiver)
	{
		try
		{
			receiver.Outbox.Add(queued);
		}
		catch (InvalidOperationException)
		{
			queued.Release(receiver.Name);
		}

	}

	private void EndureReceiverLoop(ReceiverConnection receiver)
	{
		if (_receiverLoops.TryAdd(receiver, 0))
		{
			_ = Task.Run(() => RunReceiverLoop(receiver));
		}
	}

     // Loop care ia mesaje din Outbox si le trimite catre receptor.
     private void RunReceiverLoop(ReceiverConnection receiver)
	{
		foreach (QueuedMessage queued in receiver.Outbox.GetConsumingEnumerable())
		{
			try
			{
				DeliverAttempt(queued, receiver);
               }
			catch (Exception ex)
			{
				queued.Release(receiver.Name);
				_log.Error("delivery_error", queued.Message.CorrelationId, queued.Message.MessageId, queued.Message.MessageType, $"to={receiver.Name} {ex.Message}");
               }
          }

		_receiverLoops.TryRemove(receiver, out _);
     }

     // Incearca sa livreze mesajul catre receptor. Daca nu reuseste, il pune inapoi in Outbox dupa un delay.
     private void DeliverAttempt(QueuedMessage queued, ReceiverConnection receiver)
	{
		Message message = queued.Message;
		
		if (!receiver.IsConnected)
		{
			queued.Release(receiver.Name);
			return;
          }

		var stopwatch = Stopwatch.StartNew();
		bool acked = receiver.Deliver(message);

		if (acked)
		{
			queued.Ack(receiver.Name);
			_state.RecordAck(receiver.Name, message.MessageType);
			_log.Info("message_delivered", message.CorrelationId, message.MessageId, message.MessageType, $"to={receiver.Name}", stopwatch.Elapsed);
			return;
          }

		if (!receiver.IsConnected)
		{
			queued.Release(receiver.Name);
			_log.Warn("delivery_interrupted", message.CorrelationId, message.MessageId, message.MessageType, $"to={receiver.Name} disconnected");
			return;
          }

		int failures = queued.Failures(receiver.Name);
		_log.Warn("delivery_failed", message.CorrelationId, message.MessageId, message.MessageType, $"to={receiver.Name} attempt={failures}/{Constants.MaxDeliveryAttempts}", stopwatch.Elapsed);

		if (failures >= Constants.MaxDeliveryAttempts)
		{
			queued.GiveUp(receiver.Name);
			_state.DeadLetter(message, $"delivery failed: no ack from {receiver.Name} after max attempts");
			return;
          }

		_ = Task.Delay(Constants.RetryDelay).ContinueWith(_ => AddToOutbox(queued, receiver));
     }
}