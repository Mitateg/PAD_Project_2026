using System.Text.Json;
using Pad.Common;
using Pad.Common.Grpc;
using Pad.Common.Models;

namespace Pad.Sender;

/// <summary>
/// Construieste mesaje conform contractului din Protos/broker.proto.
///
/// Partea 2 (RPC): nu mai construim un obiect C# pe care il serializam noi in JSON,
/// ci direct un BrokerMessage generat de protoc din fisierul .proto.
/// Serializarea pe fir o face gRPC (protobuf binar), nu noi.
///
/// Payload-ul ramane JSON, dar acum calatoreste ca un simplu string in campul payload_json:
/// protobuf nu are un tip "obiect JSON oarecare", deci il trimitem ca text.
/// </summary>
public static class MessageFactory
{
    /// <summary>Mesaj cu textul scris de utilizator.</summary>
    public static BrokerMessage FromText(string messageType, string text, string? correlationId = null)
    {
        return new BrokerMessage
        {
            // In proto3 campurile nu au valori implicite "inteligente", deci le completam noi
            // pe toate aici. Daca unul lipseste, broker-ul raspunde NACK missing_field:...
            MessageId = Guid.NewGuid().ToString(),
            CorrelationId = correlationId ?? Guid.NewGuid().ToString(),
            MessageType = messageType,
            SchemaVersion = 1,
            OccurredAt = DateTime.UtcNow.ToString("o"),
            PayloadJson = JsonSerializer.Serialize(new { text }, Json.Options),
        };
    }

    /// <summary>
    /// Mesaj de schimbare a unui angajat (Lab 3), conform contracts/warehouse-v1.md:
    ///   payloadJson = {"op":"upsert","employee":{...}}
    ///
    /// OccurredAt este momentul schimbarii. Bridge-ul il copiaza in employee.updatedAt inainte
    /// de PUT, iar warehouse-ul il foloseste ca timp al scrierii: o schimbare mai veche nu mai
    /// suprascrie una mai noua, chiar daca ajunge dupa ea (brokerul nu garanteaza ordinea).
    /// </summary>
    public static BrokerMessage EmployeeChange(Employee employee, string? correlationId = null)
    {
        var payload = new { op = "upsert", employee };

        return new BrokerMessage
        {
            MessageId = Guid.NewGuid().ToString(),
            CorrelationId = correlationId ?? Guid.NewGuid().ToString(),
            MessageType = Constants.EmployeeChangeType,
            SchemaVersion = 1,
            OccurredAt = DateTime.UtcNow.ToString("o"),
            PayloadJson = JsonSerializer.Serialize(payload, Json.Options),
        };
    }

    /// <summary>Un angajat de proba, cu id si nume alese aleator (pentru demo si incarcare).</summary>
    public static Employee SampleEmployee(Random random)
    {
        string[] prenume = { "Ion", "Ana", "Mihai", "Elena", "Radu", "Maria", "Vlad", "Irina" };
        string[] nume = { "Popescu", "Ionescu", "Rusu", "Ciobanu", "Lungu", "Munteanu" };
        string[] pozitii = { "Engineer", "Tester", "Analyst", "Designer", "Manager" };
        string[] departamente = { "R&D", "QA", "Sales", "Support", "IT" };

        return new Employee
        {
            Id = random.Next(1, 1000),
            Name = $"{prenume[random.Next(prenume.Length)]} {nume[random.Next(nume.Length)]}",
            Position = pozitii[random.Next(pozitii.Length)],
            Department = departamente[random.Next(departamente.Length)],
            // UpdatedAt il lasam gol: il pune bridge-ul din OccurredAt, sau warehouse-ul la primire.
        };
    }

    /// <summary>Mesaj generat automat, cu un text de proba potrivit tipului.</summary>
    public static BrokerMessage Sample(string messageType, string? correlationId = null)
    {
        string text = messageType switch
        {
            "Anunt"  => "Maine nu avem laborator",
            "Alerta" => "Serverul de la facultate este picat",
            _        => $"Salut {messageType}, acesta este un mesaj privat de test",
        };

        return FromText(messageType, text, correlationId);
    }
}
