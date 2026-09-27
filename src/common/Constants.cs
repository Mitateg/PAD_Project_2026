namespace Pad.Common;

/// <summary>
/// Valori convenite de toata echipa. Daca se schimba ceva aici, se schimba pentru toti.
/// </summary>
public static class Constants
{
    /// <summary>Portul pe care asculta broker-ul.</summary>
    public const int BrokerPort = 4242;

    /// <summary>Portul de rezerva daca 4242 este ocupat.</summary>
    public const int BrokerFallbackPort = 4243;

    /// <summary>
    /// Tipurile "publice": un mesaj de acest tip ajunge la toti receiver-ii abonati la el.
    /// Pe langa ele, fiecare receiver are un canal privat cu numele lui (ex. "ion-1"),
    /// la care este abonat automat si de la care nu se poate dezabona.
    /// Orice alt tip primeste NACK "unknown_type".
    /// </summary>
    public static readonly string[] KnownMessageTypes =
    {
        "Anunt",
        "Alerta",
    };

    /// <summary>De cate ori incercam sa livram un mesaj inainte de dead-letter.</summary>
    public const int MaxDeliveryAttempts = 3;

    /// <summary>Pauza intre doua incercari de livrare.</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    /// <summary>Cat asteptam un ACK inainte sa consideram incercarea esuata.</summary>
    public static readonly TimeSpan AckTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Rolurile pe care le poate declara un client in mesajul HELLO.</summary>
    public const string RoleSender = "sender";
    public const string RoleReceiver = "receiver";

    /// <summary>Valorile campului "status" din raspunsul broker-ului.</summary>
    public const string StatusAck = "ACK";
    public const string StatusNack = "NACK";
}
