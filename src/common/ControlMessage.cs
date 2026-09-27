namespace Pad.Common;

/// <summary>
/// Comanda trimisa de receiver catre broker DUPA HELLO, ca sa-si schimbe abonarile din mers:
///   {"action":"subscribe","type":"Mesaj"}
///   {"action":"unsubscribe","type":"Mesaj"}
/// Sender-ul o foloseste ca sa afle cine e conectat:
///   {"action":"list_receivers"}  ->  broker raspunde {"receivers":["consumer-A","consumer-B"]}
/// Broker-ul o deosebeste de un ACK / mesaj dupa campul "action".
/// </summary>
public class ControlMessage
{
    public const string Subscribe = "subscribe";
    public const string Unsubscribe = "unsubscribe";
    public const string ListReceivers = "list_receivers";

    public string Action { get; set; } = "";
    public string Type { get; set; } = "";
}

/// <summary>Raspunsul broker-ului la "list_receivers".</summary>
public class ReceiverListReply
{
    public List<string> Receivers { get; set; } = new();
}
