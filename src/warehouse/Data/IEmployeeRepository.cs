using Pad.Common.Models;
using Pad.Warehouse.Models;

namespace Pad.Warehouse.Data;

/// <summary>
/// Locul unde stau datele. Controller-ul si serviciul nu stiu daca e memorie sau Cassandra:
/// se schimba o singura linie in Program.cs.
///
/// occurredAt = momentul schimbarii (UTC). Daca lipseste, se foloseste "acum".
/// O scriere cu occurredAt mai vechi decat ce exista deja NU suprascrie datele mai noi
/// (last-write-wins), ca mesajele sosite in alta ordine sa nu strice datele bune.
/// </summary>
public interface IEmployeeRepository
{
    Task<Employee?> GetAsync(int id);

    Task<IReadOnlyList<Employee>> ListAsync(int offset, int limit);

    /// <summary>Insert sau inlocuire (upsert). Created = true doar daca id-ul nu exista inainte.</summary>
    Task<(Employee Employee, bool Created)> UpsertAsync(Employee employee, DateTime? occurredAt = null);

    /// <summary>Modifica doar campurile non-null. Returneaza null daca id-ul nu exista.</summary>
    Task<Employee?> PatchAsync(int id, EmployeeUpdate update, DateTime? occurredAt = null);

    /// <summary>Angajatii schimbati dupa momentul dat (pentru /update/employees/?since=).</summary>
    Task<IReadOnlyList<Employee>> ChangedSinceAsync(DateTimeOffset since);

    /// <summary>true doar daca stocarea chiar raspunde (pentru /health).</summary>
    Task<bool> IsHealthyAsync();
}
