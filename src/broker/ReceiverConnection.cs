using System.Collections.Concurrent;
using System.Net.Sockets;
using Pad.Common;

namespace Pad.Broker;

// 
public class ReceiverConnection
{
	public string Name { get; }
	public HashSet<string> Subscriptions { get; }
	public Socket Socket { get; }

	// ACK-urile venite de la receiver
	public BlockingCollection<BrokerReply> Acks { get; } = new();

	// Lock pentru a livra un singur mesaj o data la un receptor
	public readonly object _deliveryLock = new();

	public bool IsConnected { get; private set; } = true;

	public ReceiverConnection(string name, IEnumerable<string> subscriptions, Socket socket)
	{
		Name = name;
		Subscriptions = new HashSet<string>(subscriptions);
		Socket = socket;
     }


     // Livraram un mesaj catre receptor, asteptam ACK-ul; true - primit, false - la timeout si receptor deconectat
     public bool Deliver(Message message)
	{
		lock(_deliveryLock)
		{
			if (!IsConnected)
				return false;
			try
			{
				LineReader.WriteJson(Socket, message);
               }
			catch (SocketException)
			{
                    // Receptorul s-a deconectat
                    return false;
			}

               // Asteptam ACK, daca receptorul se deconecteaza, ClientHelper apeleaza
               // Acks.CompleteAdding() si TryTake va returna false
               while (Acks.TryTake(out BrokerReply? ack, Constants.AckTimeout))
			{
				if (ack.MessageId == message.MessageId) return ack.IsAck; // ACK pentru alt mesaj (intarziat), il ignoram si asteptam

               }

			return false;
          }
     }

	public void MarkDisconnected()
	{
		IsConnected = false;
		Acks.CompleteAdding();
     }
}

