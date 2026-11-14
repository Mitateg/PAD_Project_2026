using Pad.Common.Models;
using Pad.Warehouse.Data;
using Pad.Warehouse.Models;

namespace Pad.Warehouse.Services;

/// <summary>Cerere invalida (id, offset, limit etc.). Se transforma in HTTP 400 de BadRequestFilter.</summary>
public class BadRequestException : Exception
{
    public BadRequestException(string message) : base(message) { }
}

/// <summary>
/// Regulile: validari, paginare. Nu stie nimic despre HTTP si nici despre unde se salveaza datele.
/// Nu tine stare intre cereri (singura stare e in repository), deci poate fi folosit din orice fir.
/// </summary>
public class EmployeeService
{
    public const int DefaultLimit = 10;

    /// <summary>
    /// Maximul de randuri pe pagina. Un limit mai mare NU da eroare: este redus la MaxLimit
    /// (decizie documentata in README).
    /// </summary>
    public const int MaxLimit = 100;

    private readonly IEmployeeRepository _repo;

    public EmployeeService(IEmployeeRepository repo) => _repo = repo;

    private static void RequireId(int id)
    {
        if (id <= 0)
            throw new BadRequestException("id must be a positive integer");
    }

    public Task<Employee?> GetAsync(int id)
    {
        RequireId(id);
        return _repo.GetAsync(id);
    }

    public Task<IReadOnlyList<Employee>> ListAsync(int? offset, int? limit)
    {
        int o = offset ?? 0;
        int l = limit ?? DefaultLimit;

        if (o < 0) throw new BadRequestException("offset must be >= 0");
        if (l < 1) throw new BadRequestException("limit must be >= 1");

        return _repo.ListAsync(o, Math.Min(l, MaxLimit));
    }

    /// <summary>
    /// PUT = upsert idempotent. Acelasi PUT de doua ori da acelasi rezultat (a doua oara "exista deja").
    /// Daca employee.UpdatedAt vine completat (bridge-ul pune acolo OccurredAt), el este momentul scrierii.
    /// </summary>
    public Task<(Employee Employee, bool Created)> PutAsync(Employee employee)
    {
        RequireId(employee.Id);
        if (string.IsNullOrWhiteSpace(employee.Name))
            throw new BadRequestException("name is required");

        DateTime? occurredAt = employee.UpdatedAt == default ? null : employee.UpdatedAt;
        return _repo.UpsertAsync(employee, occurredAt);
    }

    public Task<Employee?> PatchAsync(int id, EmployeeUpdate update)
    {
        RequireId(id);
        return _repo.PatchAsync(id, update);
    }

    /// <summary>
    /// Pull-request: ce s-a schimbat dupa un moment. Momentul vine din antetul If-Modified-Since
    /// (asa il cere enuntul laboratorului); daca antetul lipseste, se foloseste parametrul ?since=.
    /// Daca vin amandoua, castiga antetul. Un antet care nu este o data valida da 400.
    /// </summary>
    public Task<IReadOnlyList<Employee>> ChangedSinceAsync(string? ifModifiedSince, DateTimeOffset? since)
    {
        if (!string.IsNullOrWhiteSpace(ifModifiedSince))
        {
            if (!HttpDates.TryParse(ifModifiedSince, out DateTimeOffset fromHeader))
                throw new BadRequestException(
                    "If-Modified-Since is not a valid date (ex. Sat, 29 Oct 2014 19:43:31 GMT)");

            return ChangedSinceAsync(fromHeader);
        }

        return ChangedSinceAsync(since);
    }

    public Task<IReadOnlyList<Employee>> ChangedSinceAsync(DateTimeOffset? since)
    {
        if (since is null)
            throw new BadRequestException(
                "If-Modified-Since header or since parameter is required (ex. Sat, 29 Oct 2014 19:43:31 GMT, or since=2026-11-09T10:00:00Z)");

        return _repo.ChangedSinceAsync(since.Value);
    }

    /// <summary>true doar daca stocarea chiar raspunde (Cassandra, in varianta cu Cassandra).</summary>
    public Task<bool> IsHealthyAsync() => _repo.IsHealthyAsync();
}
