namespace Pad.Common.Models;

/// <summary>
/// Angajatul din warehouse. Modelul este folosit de toate componentele (warehouse, proxy, client, bridge).
///
/// Ramane o CLASA cu proprietati publice get/set si constructor fara parametri:
/// serializatorul XML al ASP.NET (XmlSerializer) nu stie sa construiasca record-uri
/// sau clase cu constructor cu parametri si ar returna continut gol.
///
/// JSON (camelCase): {"id":1,"name":"Ion Popescu","position":"Engineer","department":"R&amp;D","updatedAt":"2026-11-09T10:00:00Z"}
/// </summary>
public class Employee
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Position { get; set; } = "";
    public string Department { get; set; } = "";

    /// <summary>
    /// Momentul ultimei schimbari, in UTC. La PUT, daca clientul il trimite (ex. bridge-ul copiaza aici
    /// OccurredAt-ul mesajului din broker), warehouse-ul il foloseste ca "timp al scrierii":
    /// o schimbare mai veche nu mai suprascrie una mai noua, chiar daca ajunge dupa ea.
    /// Daca lipseste, warehouse-ul pune momentul primirii cererii.
    /// </summary>
    public DateTime UpdatedAt { get; set; }
}
