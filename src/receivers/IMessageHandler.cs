using Pad.Common.Grpc;

namespace Pad.Receiver;

public interface IMessageHandler
{
	Task<bool> HandleAsync(BrokerMessage m, bool isPrivate);   // true = ACK
}