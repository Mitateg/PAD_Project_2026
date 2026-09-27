using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Pad.Common;

namespace Pad.Broker;

public class ClientHandler
{
	private readonly Socket _socket;
	private readonly BrokerState _state;
	private readonly Logger _log;
	private readonly LineReader _reader;

	public ClientHandler(Socket socket, BrokerState state, Logger log)
	{
		_socket = socket;
		_state = state;
		_log = log;
		_reader = new LineReader(socket);
     }

	public void Run()
	{ 
		string remote = _socket.RemoteEndPoint?.ToString() ?? "unknown";
		try
		{
			HelloMessage? hello = ReadHello();
			if (hello == null) return; // Clientul s-a deconectat imediat

			_log.Info("client_hello", result: $"{hello.Role} {hello.Name} from {remote}");

			if (hello.Role == Pad.Common.Constants.RoleReceiver)
				RunReceiverLoop(hello);
			else
				RunSenderLoop(hello);
          }
		catch (SocketException ex)
		{
			_log.Warn("client_error", result: $"{remote} {ex.Message}");
		}
		finally
		{
			_socket.Close();
		}
     }

	private HelloMessage? ReadHello()
	{
		string? line = _reader.ReadLine();
		if ( line == null)
			return null;

		HelloMessage? hello = Json.TryDeserialize<HelloMessage>(line);
		if (hello == null || string.IsNullOrWhiteSpace(hello.Role))
		{
			LineReader.WriteJson(_socket, BrokerReply.Nack(null, "invalid_hello"));
			_log.Warn("invalid_hello", result: line);
			return null;
          }

		return hello;
     }

	private void RunSenderLoop(HelloMessage hello)
	{
		while (true)
		{
			string? line = _reader.ReadLine();
			if (line == null) break; // Senderul a inchis conexiunea

               // Senderul poate cere lista de receptori conectati, pentru a alege unul
               ControlMessage? control = Json.TryDeserialize<ControlMessage>(line);
			if (control != null && control.Action == ControlMessage.ListReceivers)
			{
				LineReader.WriteJson(_socket, new ReceiverListReply { Receivers = _state.ConnectedReceiverNames() });
				continue;
			}

			var stopwatch = Stopwatch.StartNew();

               // Un Json valid, dar care nu respecta schema de mesaj, va fi respins cu un nack
               if (MessageValidator.TryParse(line, out Message? message, out string? reason, _state.KnownReceiverNames()))
			{
				_state.Enqueue(message!);
				LineReader.WriteJson(_socket, BrokerReply.Ack(message!.MessageId));
				_log.Info("message_received", message.CorrelationId, message.MessageId, message.MessageType, "ok", stopwatch.Elapsed);
               }
			else
			{
                    // Incercam sa extragem messageId din mesajul invalid, pentru a putea trimite un nack corespunzator
                    string? messageId = Json.TryDeserialize<Message>(line)?.MessageId;
				LineReader.WriteJson(_socket, BrokerReply.Nack(messageId, reason!));
				_log.Warn("message_rejected", msg: messageId, result: $"nack reason={reason}", duration: stopwatch.Elapsed);
               }
          }
		_log.Info("sender_disconnected", result: hello.Name);
     }

	private void RunReceiverLoop(HelloMessage hello)
	{
          // Inregistram receptorul in BrokerState si ii atribuim un nume unic, daca nu a fost specificat unul
          ReceiverConnection receiver = _state.RegisterReceiver(hello.Name, hello.SubscribeTo, _socket);

          // Trimitem un mesaj de confirmare catre receptor, cu numele atribuit
          LineReader.WriteJson(_socket, new BrokerReply { Status = Pad.Common.Constants.StatusAck, AssignedName = receiver.Name});

		try
		{
			while (true)
			{
				string? line = _reader.ReadLine();
				if (line == null) break; // Receptorul a inchis conexiunea

                    // Receptorul poate trimite un mesaj de control pentru a-si schimba abonamentele
                    ControlMessage? control = Json.TryDeserialize<ControlMessage>(line);
				if (control != null && !string.IsNullOrEmpty(control.Action))
				{
					_state.ChangeSubscriptions(receiver, control.Action, control.Type);
					continue;
                    }

				BrokerReply? ack = Json.TryDeserialize<BrokerReply>(line);
				if (ack == null)
				{
					_log.Warn("invalid_ack", result: line);
					continue;
                    }
				receiver.Acks.Add(ack);
               }
		}
		finally
		{
			receiver.MarkDisconnected();
			_state.RemoveReceiver(receiver);
          }
     }
}
