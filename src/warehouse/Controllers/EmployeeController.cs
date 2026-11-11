using Microsoft.AspNetCore.Mvc;
using Pad.Common.Models;
using Pad.Warehouse.Models;
using Pad.Warehouse.Services;

namespace Pad.Warehouse.Controllers;

/// <summary>
/// Maparea cererilor HTTP pe operatiile serviciului. FARA logica si FARA stare intre cereri:
/// primeste, apeleaza serviciul, raspunde. Formatul (JSON / XML) il alege ASP.NET dupa antetul Accept.
///
/// Caile sunt absolute (cu "/" in fata), ca sa corespunda exact contractului:
/// /employee/?id=1 si /employee?id=1 ajung amandoua aici (routing-ul ignora slash-ul final).
/// </summary>
[ApiController]
public class EmployeeController : ControllerBase
{
    private readonly EmployeeService _service;

    public EmployeeController(EmployeeService service) => _service = service;

    // GET /employee/?id=1  ->  200 / 404 / 400
    [HttpGet("/employee")]
    public async Task<ActionResult<Employee>> Get([FromQuery] int id)
    {
        Employee? employee = await _service.GetAsync(id);
        return employee is null ? NotFound() : Ok(employee);
    }

    // GET /employees/?offset=0&limit=10  ->  200 (lista, eventual goala) / 400
    [HttpGet("/employees")]
    public async Task<ActionResult<List<Employee>>> List([FromQuery] int? offset, [FromQuery] int? limit)
    {
        IReadOnlyList<Employee> page = await _service.ListAsync(offset, limit);
        return Ok(page.ToList());
    }

    // PUT /employee/  (corp: angajatul)  ->  201 prima oara, 200 daca exista deja. Idempotent.
    [HttpPut("/employee")]
    public async Task<ActionResult<Employee>> Put([FromBody] Employee body)
    {
        var (employee, created) = await _service.PutAsync(body);
        return created ? StatusCode(StatusCodes.Status201Created, employee) : Ok(employee);
    }

    // POST /employee/?id=1  (corp: doar campurile de schimbat)  ->  200 / 404 / 400
    [HttpPost("/employee")]
    public async Task<ActionResult<Employee>> Post([FromQuery] int id, [FromBody] EmployeeUpdate body)
    {
        Employee? employee = await _service.PatchAsync(id, body);
        return employee is null ? NotFound() : Ok(employee);
    }

    // GET /update/employees/  cu antetul  If-Modified-Since: Sat, 29 Oct 2014 19:43:31 GMT
    //   ->  ce s-a schimbat dupa acel moment (metoda "pull-request" din enunt).
    // Varianta alternativa, fara antet: GET /update/employees/?since=2026-11-09T10:00:00Z
    [HttpGet("/update/employees")]
    public async Task<ActionResult<List<Employee>>> Since(
        [FromHeader(Name = "If-Modified-Since")] string? ifModifiedSince,
        [FromQuery] DateTimeOffset? since)
    {
        IReadOnlyList<Employee> changed = await _service.ChangedSinceAsync(ifModifiedSince, since);
        return Ok(changed.ToList());
    }

    // GET /health  ->  200 doar daca stocarea chiar raspunde, altfel 503.
    // Proxy-ul si Docker se bazeaza pe asta ca sa nu trimita trafic spre un nod mort.
    [HttpGet("/health")]
    public async Task<IActionResult> Health()
    {
        return await _service.IsHealthyAsync()
            ? Ok()
            : StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
}
