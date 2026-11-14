using Cassandra;
using Pad.Common.Models;
using Pad.Warehouse.Models;

// ASP.NET are si el un ISession (sesiunea HTTP), deci numim explicit pe cel din Cassandra.
using ISession = Cassandra.ISession;

namespace Pad.Warehouse.Data;

/// <summary>
/// A doua varianta: datele stau in Cassandra, partajate de toate nodurile warehouse.
/// Aceleasi operatii ca InMemoryEmployeeRepository, deci controller-ul si serviciul nu se schimba.
///
/// Despre ordine: fiecare scriere poarta "USING TIMESTAMP" = momentul schimbarii. Cassandra pastreaza
/// scrierea cu timestamp-ul cel mai mare, indiferent in ce ordine au ajuns mesajele (brokerul nu garanteaza ordinea).
/// </summary>
public class CassandraEmployeeRepository : IEmployeeRepository
{
    private const string Columns = "id, name, position, department, updated_at";

    private readonly ISession _session;
    private readonly PreparedStatement _get;
    private readonly PreparedStatement _exists;
    private readonly PreparedStatement _insert;
    private readonly PreparedStatement _logChange;
    private readonly PreparedStatement _list;
    private readonly PreparedStatement _changedIds;

    public CassandraEmployeeRepository(ISession session)
    {
        _session = session;

        // Instructiuni pregatite: se parseaza o singura data, iar valorile se leaga, nu se concateneaza in text.
        _get = session.Prepare($"SELECT {Columns} FROM pad.employees WHERE id = ?");
        _exists = session.Prepare("SELECT id FROM pad.employees WHERE id = ?");
        _insert = session.Prepare(
            "INSERT INTO pad.employees (id, name, position, department, updated_at) VALUES (?, ?, ?, ?, ?) USING TIMESTAMP ?");
        _logChange = session.Prepare(
            "INSERT INTO pad.employee_changes (bucket, changed_at, employee_id) VALUES ('all', now(), ?)");
        _list = session.Prepare($"SELECT {Columns} FROM pad.employees LIMIT ?");
        _changedIds = session.Prepare(
            "SELECT employee_id FROM pad.employee_changes WHERE bucket = 'all' AND changed_at > maxTimeuuid(?)");
    }

    private static Employee Map(Row row) => new()
    {
        Id = row.GetValue<int>("id"),
        Name = row.GetValue<string>("name") ?? "",
        Position = row.GetValue<string>("position") ?? "",
        Department = row.GetValue<string>("department") ?? "",
        UpdatedAt = row.IsNull("updated_at") ? default : row.GetValue<DateTimeOffset>("updated_at").UtcDateTime,
    };

    public async Task<Employee?> GetAsync(int id)
    {
        RowSet rows = await _session.ExecuteAsync(_get.Bind(id));
        Row? row = rows.FirstOrDefault();
        return row is null ? null : Map(row);
    }

    public async Task<IReadOnlyList<Employee>> ListAsync(int offset, int limit)
    {
        // LIMITARE: Cassandra nu are OFFSET. Citim primele offset+limit randuri si le sarim pe primele offset.
        // La scara laboratorului e acceptabil; la offset-uri mari ar fi lent. Ordinea este cea a token-urilor
        // cheii primare (stabila intre cereri, dar nu sortata dupa id).
        RowSet rows = await _session.ExecuteAsync(_list.Bind(offset + limit));
        return rows.Skip(offset).Take(limit).Select(Map).ToList();
    }

    public async Task<(Employee Employee, bool Created)> UpsertAsync(Employee employee, DateTime? occurredAt = null)
    {
        DateTime when = TimeUtil.AsUtc(occurredAt ?? DateTime.UtcNow);

        // INSERT este deja upsert in Cassandra, deci idempotenta vine "gratis".
        // Verificarea de mai jos serveste doar ca sa deosebim 201 (nou) de 200 (exista deja).
        bool created = (await _session.ExecuteAsync(_exists.Bind(employee.Id))).FirstOrDefault() is null;

        await _session.ExecuteAsync(_insert.Bind(
            employee.Id,
            employee.Name,
            employee.Position,
            employee.Department,
            new DateTimeOffset(when),
            TimeUtil.ToMicros(when)));

        await _session.ExecuteAsync(_logChange.Bind(employee.Id));

        // Returnam ce este de fapt stocat: daca scrierea a fost mai veche decat cea existenta, ramane cea noua.
        Employee stored = (await GetAsync(employee.Id))!;
        return (stored, created);
    }

    public async Task<Employee?> PatchAsync(int id, EmployeeUpdate update, DateTime? occurredAt = null)
    {
        // UPDATE in Cassandra ar CREA randul daca nu exista, deci verificam intai.
        if (await GetAsync(id) is null)
            return null;

        DateTime when = TimeUtil.AsUtc(occurredAt ?? DateTime.UtcNow);

        // Doar campurile primite intra in SET. In text intra numai nume de coloane fixe; valorile sunt legate.
        var assignments = new List<string>();
        var values = new List<object> { TimeUtil.ToMicros(when) };   // "USING TIMESTAMP ?" apare primul in text
        if (update.Name is not null) { assignments.Add("name = ?"); values.Add(update.Name); }
        if (update.Position is not null) { assignments.Add("position = ?"); values.Add(update.Position); }
        if (update.Department is not null) { assignments.Add("department = ?"); values.Add(update.Department); }
        assignments.Add("updated_at = ?");
        values.Add(new DateTimeOffset(when));
        values.Add(id);

        string cql = $"UPDATE pad.employees USING TIMESTAMP ? SET {string.Join(", ", assignments)} WHERE id = ?";
        await _session.ExecuteAsync(new SimpleStatement(cql, values.ToArray()));

        await _session.ExecuteAsync(_logChange.Bind(id));
        return await GetAsync(id);
    }

    public async Task<IReadOnlyList<Employee>> ChangedSinceAsync(DateTimeOffset since)
    {
        RowSet rows = await _session.ExecuteAsync(_changedIds.Bind(since));
        List<int> ids = rows.Select(r => r.GetValue<int>("employee_id")).Distinct().ToList();

        var result = new List<Employee>();
        foreach (int id in ids)
        {
            Employee? employee = await GetAsync(id);
            if (employee is not null)
                result.Add(employee);
        }
        return result.OrderBy(e => e.Id).ToList();
    }

    public async Task<bool> IsHealthyAsync()
    {
        // Intreaba chiar Cassandra: un /health care raspunde 200 fara sa verifice ar trimite trafic spre un nod mort.
        try
        {
            await _session.ExecuteAsync(new SimpleStatement("SELECT release_version FROM system.local"));
            return true;
        }
        catch
        {
            return false;
        }
    }
}
