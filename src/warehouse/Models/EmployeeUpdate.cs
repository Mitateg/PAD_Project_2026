namespace Pad.Warehouse.Models;

/// <summary>
/// Corpul unui POST /employee/?id=N: modificare partiala.
/// Un camp lipsa (null) inseamna "nu-l atinge", deci {"name":"X"} nu sterge pozitia si departamentul.
/// </summary>
public class EmployeeUpdate
{
    public string? Name { get; set; }
    public string? Position { get; set; }
    public string? Department { get; set; }
}
