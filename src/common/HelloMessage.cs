namespace Pad.Common;

/// <summary>
/// Primul mesaj trimis de orice client dupa conectare. Ii spune broker-ului cine este.
/// Sender:   {"role":"sender","name":"sender-1"}
/// Receiver: {"role":"receiver","name":"consumer-A","subscribeTo":["Mesaj"]}
/// </summary>
public class HelloMessage
{
    public string Role { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>Doar pentru receiver: tipurile de mesaje la care se aboneaza.</summary>
    public List<string> SubscribeTo { get; set; } = new();
}
