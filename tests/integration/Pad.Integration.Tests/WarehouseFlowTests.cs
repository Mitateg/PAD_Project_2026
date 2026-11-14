using System.Net;
using System.Net.Http.Json;
using System.Text;
using Pad.Common.Models;
using Xunit;

namespace Pad.Integration.Tests;

/// <summary>
/// Teste de integrare pentru warehouse: cereri HTTP reale catre un server pornit pe un port liber.
/// Fiecare test porneste propriul warehouse (xunit creeaza o instanta noua a clasei per test),
/// deci testele nu-si vad datele intre ele.
/// </summary>
public class WarehouseFlowTests : IAsyncLifetime
{
    private TestWarehouse _warehouse = null!;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        _warehouse = await TestWarehouse.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri(_warehouse.Address) };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _warehouse.DisposeAsync();
    }

    private static Employee Emp(int id, string name = "Ion Popescu", DateTime? updatedAt = null) => new()
    {
        Id = id,
        Name = name,
        Position = "Engineer",
        Department = "R&D",
        UpdatedAt = updatedAt ?? default,
    };

    private Task<HttpResponseMessage> Put(Employee e) => _http.PutAsJsonAsync("/employee/", e);

    private Task<HttpResponseMessage> GetAccept(string url, string accept)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd(accept);
        return _http.SendAsync(request);
    }

    // Pull-request ca in enunt: GET /update/employees/ cu antetul If-Modified-Since.
    // TryAddWithoutValidation, ca sa putem trimite si valori pe care .NET le-ar respinge (data invalida, zi gresita).
    private Task<HttpResponseMessage> GetIfModifiedSince(string value)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/update/employees/");
        request.Headers.TryAddWithoutValidation("If-Modified-Since", value);
        request.Headers.Accept.ParseAdd("application/json");
        return _http.SendAsync(request);
    }

    // -----------------------------------------------------------------------
    // PUT: idempotent
    // -----------------------------------------------------------------------
    [Fact]
    public async Task Put_PrimaOaraDa201_ApoiAceeasiCerere200_FaraDublura()
    {
        var first = await Put(Emp(1));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await Put(Emp(1));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var all = await _http.GetFromJsonAsync<List<Employee>>("/employees/");
        Assert.Single(all!);
    }

    [Fact]
    public async Task Put_ScriereMaiVeche_NuSuprascrieCeaMaiNoua()
    {
        var newer = new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);
        var older = new DateTime(2026, 11, 9, 10, 0, 0, DateTimeKind.Utc);

        await Put(Emp(1, "nou", newer));
        var late = await Put(Emp(1, "vechi", older));   // ajunge dupa, in alta ordine

        Assert.Equal(HttpStatusCode.OK, late.StatusCode);
        var stored = await _http.GetFromJsonAsync<Employee>("/employee/?id=1");
        Assert.Equal("nou", stored!.Name);
        Assert.Equal(newer, stored.UpdatedAt);
    }

    [Fact]
    public async Task Put_ContentTypeNesuportat_Da415()
    {
        var response = await _http.PutAsync("/employee/", new StringContent("salut", Encoding.UTF8, "text/plain"));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Fact]
    public async Task Put_CorpInvalid_Da400()
    {
        var response = await _http.PutAsync("/employee/", new StringContent("nu e json", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Put_FaraNume_Da400()
    {
        var response = await Put(Emp(1, name: ""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // -----------------------------------------------------------------------
    // GET: coduri de eroare
    // -----------------------------------------------------------------------
    [Fact]
    public async Task Get_Inexistent_Da404()
    {
        var response = await _http.GetAsync("/employee/?id=999");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("/employee/?id=0")]
    [InlineData("/employee/?id=-3")]
    [InlineData("/employee/?id=abc")]
    [InlineData("/employee/")]
    [InlineData("/employees/?offset=-1")]
    [InlineData("/employees/?limit=0")]
    [InlineData("/update/employees/")]
    public async Task CereriInvalide_Dau400(string url)
    {
        var response = await _http.GetAsync(url);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // -----------------------------------------------------------------------
    // Paginare
    // -----------------------------------------------------------------------
    [Fact]
    public async Task List_FaraParametri_Da10_SiLimitPesteMaxim_EsteRedusLa100()
    {
        await Task.WhenAll(Enumerable.Range(1, 120).Select(i => Put(Emp(i))));

        var implicitPage = await _http.GetFromJsonAsync<List<Employee>>("/employees/");
        var capped = await _http.GetFromJsonAsync<List<Employee>>("/employees/?limit=5000");
        var lastPage = await _http.GetFromJsonAsync<List<Employee>>("/employees/?offset=100&limit=50");

        Assert.Equal(10, implicitPage!.Count);
        Assert.Equal(100, capped!.Count);
        Assert.Equal(20, lastPage!.Count);
    }

    // -----------------------------------------------------------------------
    // JSON / XML (continutul il alege clientul prin Accept)
    // -----------------------------------------------------------------------
    [Fact]
    public async Task Accept_Json_ReturneazaJson()
    {
        await Put(Emp(1));

        var response = await GetAccept("/employee/?id=1", "application/json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        Assert.Contains("\"name\":\"Ion Popescu\"", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Accept_Xml_ReturneazaXml()
    {
        await Put(Emp(1));

        var response = await GetAccept("/employee/?id=1", "application/xml");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/xml", response.Content.Headers.ContentType!.MediaType);
        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("<Name>Ion Popescu</Name>", body);
    }

    [Fact]
    public async Task Accept_Nesuportat_Da406()
    {
        await Put(Emp(1));

        var response = await GetAccept("/employee/?id=1", "text/plain");

        Assert.Equal(HttpStatusCode.NotAcceptable, response.StatusCode);
    }

    [Fact]
    public async Task Put_CuCorpXml_FunctioneazaLaFel()
    {
        const string xml = "<Employee><Id>7</Id><Name>Ana</Name><Position>Dev</Position><Department>IT</Department></Employee>";

        var response = await _http.PutAsync("/employee/", new StringContent(xml, Encoding.UTF8, "application/xml"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var stored = await _http.GetFromJsonAsync<Employee>("/employee/?id=7");
        Assert.Equal("Ana", stored!.Name);
    }

    // -----------------------------------------------------------------------
    // POST: doar campurile trimise
    // -----------------------------------------------------------------------
    [Fact]
    public async Task Post_DoarNumele_PastreazaPozitiaSiDepartamentul()
    {
        await Put(Emp(1));

        var response = await _http.PostAsJsonAsync("/employee/?id=1", new { name = "Nume Nou" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await _http.GetFromJsonAsync<Employee>("/employee/?id=1");
        Assert.Equal("Nume Nou", stored!.Name);
        Assert.Equal("Engineer", stored.Position);
        Assert.Equal("R&D", stored.Department);
    }

    [Fact]
    public async Task Post_Inexistent_Da404()
    {
        var response = await _http.PostAsJsonAsync("/employee/?id=999", new { name = "X" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // -----------------------------------------------------------------------
    // /update/employees/?since=  (pull-request)
    // -----------------------------------------------------------------------
    [Fact]
    public async Task Since_ReturneazaDoarAngajatiiSchimbatiDupaMoment()
    {
        await Put(Emp(1));
        await Task.Delay(200);
        var since = DateTimeOffset.UtcNow;
        await Task.Delay(200);
        await Put(Emp(2));

        var changed = await _http.GetFromJsonAsync<List<Employee>>(
            "/update/employees/?since=" + Uri.EscapeDataString(since.ToString("O")));

        Assert.Equal(new[] { 2 }, changed!.Select(e => e.Id).ToArray());
    }

    [Fact]
    public async Task Since_AntetIfModifiedSince_ReturneazaDoarAngajatiiSchimbatiDupaMoment()
    {
        // Data HTTP are rezolutie de o secunda, deci lasam mai mult de o secunda de fiecare parte a momentului ales.
        await Put(Emp(1));
        await Task.Delay(1200);
        var since = DateTimeOffset.UtcNow;
        await Task.Delay(1200);
        await Put(Emp(2));

        var response = await GetIfModifiedSince(since.UtcDateTime.ToString("R", System.Globalization.CultureInfo.InvariantCulture));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var changed = await response.Content.ReadFromJsonAsync<List<Employee>>();
        Assert.Equal(new[] { 2 }, changed!.Select(e => e.Id).ToArray());
    }

    [Fact]
    public async Task Since_ExempluDinEnunt_CuZiGresita_NuDa400()
    {
        await Put(Emp(1));

        var response = await GetIfModifiedSince("Sat, 29 Oct 2014 19:43:31 GMT");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var changed = await response.Content.ReadFromJsonAsync<List<Employee>>();
        Assert.Equal(new[] { 1 }, changed!.Select(e => e.Id).ToArray());   // totul s-a schimbat dupa 2014
    }

    [Fact]
    public async Task Since_AntetInvalid_Da400()
    {
        var response = await GetIfModifiedSince("nu e o data");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // -----------------------------------------------------------------------
    // Concurenta: mai multe cereri simultane, toate procesate corect
    // -----------------------------------------------------------------------
    [Fact]
    public async Task Put_50DeCereriInParalel_ToateSuntSalvate()
    {
        var responses = await Task.WhenAll(Enumerable.Range(1, 50).Select(i => Put(Emp(i, $"Angajat {i}"))));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        for (int i = 1; i <= 50; i++)
        {
            var stored = await _http.GetFromJsonAsync<Employee>($"/employee/?id={i}");
            Assert.Equal($"Angajat {i}", stored!.Name);
        }
    }

    // -----------------------------------------------------------------------
    // /health
    // -----------------------------------------------------------------------
    [Fact]
    public async Task Health_Da200()
    {
        var response = await _http.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
