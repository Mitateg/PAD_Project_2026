using System.Net;
using System.Net.Sockets;
using Pad.Common;
using Pad.Broker;

// Punct de intrare a brokerului, care accepta conexiuni de la clienti (senderi si receptori) si porneste un dispatcher pentru a livra mesajele catre receptori
var log = new Logger("broker");
var state = new BrokerState(log);

Socket listner = CreateListner(log);

var dispatcher = new Dispatcher(state, log);
_ = Task.Run(() => dispatcher.RunForever());

while (true)
{
    Socket client = listner.Accept(); // Acceptam conexiunea de la client
     _ = Task.Run(() => new ClientHandler(client, state, log).Run());
}


static Socket CreateListner(Logger log)
{
    foreach (int port in new[] { Constants.BrokerPort, Constants.BrokerFallbackPort })
     {
          // Cream socketul TCP si il legam la portul dorit
          var listner = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
          try
          {
               listner.Bind(new IPEndPoint(IPAddress.Any, port));
               listner.Listen(backlog: 100);
               log.Info("broker_started", result: $"listening on port {port}");
               return listner;
          }
          catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
          {
               log.Warn("port_in_use", result: $"port {port} is already in use, trying next");
               listner.Close();
          }
     }

    throw new InvalidOperationException("Could not bind to any port");
}