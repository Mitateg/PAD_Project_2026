using Pad.Common.Grpc;

namespace Pad.Receiver;

public class ConsoleMessageHandler : IMessageHandler
{
	private readonly MessageProcessor _processor;

	public ConsoleMessageHandler(MessageProcessor processor)
	{
		_processor = processor;
	}

	public Task<bool> HandleAsync(BrokerMessage message, bool isPrivate)
	{
		_processor.Process(message, isPrivate);

		// Mesajul este considerat procesat, inclusiv daca este duplicat.
		return Task.FromResult(true);
	}
}