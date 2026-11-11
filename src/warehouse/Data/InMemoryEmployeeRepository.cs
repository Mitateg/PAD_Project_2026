using System.Collections.Concurrent;
using Pad.Common.Models;
using Pad.Warehouse.Models;

namespace Pad.Warehouse.Data;

/// <summary>
/// Prima varianta: datele stau in memoria acestui proces. Nu supravietuieste unui restart
/// si doua noduri warehouse NU impart date (de aia exista varianta Cassandra).
/// Util ca echipa sa poata testa imediat, fara baza de date.
/// </summary>
public class InMemoryEmployeeRepository : IEmployeeRepository
{
    private readonly ConcurrentDictionary<int, Employee> _items = new();

    // Scrierile sunt "citeste-verifica-scrie", deci le serializam; citirile raman fara lock.
    private readonly object _writeLock = new();

    // Returnam mereu copii, ca un apelant sa nu poata modifica din afara obiectele stocate.
    private static Employee Clone(Employee e) => new()
    {
        Id = e.Id,
        Name = e.Name,
        Position = e.Position,
        Department = e.Department,
        UpdatedAt = e.UpdatedAt,
    };

    public Task<Employee?> GetAsync(int id) =>
        Task.FromResult(_items.TryGetValue(id, out Employee? e) ? Clone(e) : null);

    public Task<IReadOnlyList<Employee>> ListAsync(int offset, int limit)
    {
        IReadOnlyList<Employee> page = _items.Values
            .OrderBy(e => e.Id)
            .Skip(offset)
            .Take(limit)
            .Select(Clone)
            .ToList();
        return Task.FromResult(page);
    }

    public Task<(Employee Employee, bool Created)> UpsertAsync(Employee employee, DateTime? occurredAt = null)
    {
        DateTime when = TimeUtil.AsUtc(occurredAt ?? DateTime.UtcNow);

        lock (_writeLock)
        {
            bool created = !_items.TryGetValue(employee.Id, out Employee? current);

            // Scriere mai veche decat ce avem: ramane varianta mai noua.
            if (!created && current!.UpdatedAt > when)
                return Task.FromResult((Clone(current), false));

            Employee stored = Clone(employee);
            stored.UpdatedAt = when;
            _items[stored.Id] = stored;
            return Task.FromResult((Clone(stored), created));
        }
    }

    public Task<Employee?> PatchAsync(int id, EmployeeUpdate update, DateTime? occurredAt = null)
    {
        DateTime when = TimeUtil.AsUtc(occurredAt ?? DateTime.UtcNow);

        lock (_writeLock)
        {
            if (!_items.TryGetValue(id, out Employee? current))
                return Task.FromResult<Employee?>(null);

            Employee stored = Clone(current);
            if (update.Name is not null) stored.Name = update.Name;
            if (update.Position is not null) stored.Position = update.Position;
            if (update.Department is not null) stored.Department = update.Department;
            stored.UpdatedAt = when;

            _items[id] = stored;
            return Task.FromResult<Employee?>(Clone(stored));
        }
    }

    public Task<IReadOnlyList<Employee>> ChangedSinceAsync(DateTimeOffset since)
    {
        IReadOnlyList<Employee> changed = _items.Values
            .Where(e => e.UpdatedAt > since.UtcDateTime)
            .OrderBy(e => e.Id)
            .Select(Clone)
            .ToList();
        return Task.FromResult(changed);
    }

    public Task<bool> IsHealthyAsync() => Task.FromResult(true);
}
