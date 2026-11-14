using System.Globalization;
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
    private readonly PreparedStatement _claimId;
    private readonly PreparedStatement _insert;
    private readonly PreparedStatement _logChange;
    private readonly PreparedStatement _list;
    private readonly PreparedStatement _changedIds;

    public CassandraEmployeeRepository(ISession session)
    {
        _session = session;

        // Instructiuni pregatite: se parseaza o singura data, iar valorile se leaga, nu se concateneaza in text.
        _get = session.Prepare($"SELECT {Columns} FROM pad.employees WHERE id = ?");
        _claimId = session.Prepare("INSERT INTO pad.employee_ids (id) VALUES (?) IF NOT EXISTS");
        _insert = session.Prepare(
            "INSERT INTO pad.employees (id, name, position, department, updated_at) VALUES (?, ?, ?, ?, ?) USING TIMESTAMP ?");
        _logChange = session.Prepare(
            "INSERT INTO pad.employee_changes (bucket, changed_at, employee_id) VALUES (?, now(), ?)");
        _list = session.Prepare($"SELECT {Columns} FROM pad.employees LIMIT ?");
        _changedIds = session.Prepare(
            "SELECT employee_id FROM pad.employee_changes WHERE bucket = ? AND changed_at > maxTimeuuid(?)");
    }

    // Bucket-ul unei schimbari = ziua ei (UTC).
    private static string BucketFor(DateTime utc) => utc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private Task LogChangeAsync(int id) =>
        _session.ExecuteAsync(_logChange.Bind(BucketFor(DateTime.UtcNow), id));

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
        // Ca sa deosebim 201 (nou) de 200 (exista deja) fara cursa, "revendicam" id-ul atomic: exact o cerere
        // vede [applied] = true, chiar daca mai multe PUT-uri pentru acelasi id nou sosesc in acelasi timp.
        RowSet claim = await _session.ExecuteAsync(_claimId.Bind(employee.Id));
        bool created = claim.First().GetValue<bool>("[applied]");

        await _session.ExecuteAsync(_insert.Bind(
            employee.Id,
            employee.Name,
            employee.Position,
            employee.Department,
            new DateTimeOffset(when),
            TimeUtil.ToMicros(when)));

        await LogChangeAsync(employee.Id);

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

        await LogChangeAsync(id);
        return await GetAsync(id);
    }

    public async Task<IReadOnlyList<Employee>> ChangedSinceAsync(DateTimeOffset since)
    {
        // Citim bucket-urile (zilele) de la ziua lui "since" pana azi. Incepem cu o zi mai devreme, ca o diferenta
        // mica intre ceasuri sa nu piarda o schimbare de la miezul noptii, si nu mergem mai in urma decat retentia:
        // altfel o data veche (ex. 2014 din enunt) ar insemna mii de interogari pentru bucket-uri deja sterse.
        DateTime today = DateTime.UtcNow.Date;
        DateTime earliest = today.AddDays(-CassandraSession.ChangeRetentionDays);
        DateTime first = since.UtcDateTime.Date.AddDays(-1);
        if (first < earliest) first = earliest;

        var ids = new HashSet<int>();
        for (DateTime day = first; day <= today; day = day.AddDays(1))
        {
            RowSet rows = await _session.ExecuteAsync(_changedIds.Bind(BucketFor(day), since));
            foreach (Row row in rows)
                ids.Add(row.GetValue<int>("employee_id"));
        }

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
