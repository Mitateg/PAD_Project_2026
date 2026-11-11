namespace Pad.Common;

/// <summary>
/// Valori convenite de toata echipa. Daca se schimba ceva aici, se schimba pentru toti.
/// </summary>
public static class Constants
{
     /// <summary>Portul pe care asculta broker-ul (gRPC / HTTP2).</summary>
     public const int BrokerPort = 4242;

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
        EmployeeChangeType,
    };

     /// <summary>
     /// Tipul mesajelor prin care o schimbare de angajat ajunge in warehouse
     /// (sender -> broker -> bridge receiver -> proxy -> warehouse).
     /// Payload-ul este {"op":"upsert","employee":{...}}.
     /// </summary>
     public const string EmployeeChangeType = "EmployeeChange";

     /// <summary>Portul HTTP al nodurilor warehouse (in containere). Se poate schimba cu PAD_PORT.</summary>
     public const int WarehousePort = 5000;

     /// <summary>De cate ori incercam sa livram un mesaj inainte de dead-letter.</summary>
     public const int MaxDeliveryAttempts = 3;

     /// <summary>Pauza intre doua incercari de livrare.</summary>
     public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

     /// <summary>Cat timp pastram un mesaj pe care nu l-a primit nimeni inainte de dead-letter.</summary>
     public static readonly TimeSpan MessageTtl = TimeSpan.FromMinutes(5);

     /// <summary>Cat asteptam un ACK inainte sa consideram incercarea esuata.</summary>
     public static readonly TimeSpan AckTimeout = TimeSpan.FromSeconds(5);

     /// <summary>Valorile campului "status" din raspunsul broker-ului.</summary>
     public const string StatusAck = "ACK";
     public const string StatusNack = "NACK";
}
