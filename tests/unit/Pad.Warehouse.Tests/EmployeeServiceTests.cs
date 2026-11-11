using Pad.Common.Models;
using Pad.Warehouse.Data;
using Pad.Warehouse.Models;
using Pad.Warehouse.Services;
using Xunit;

namespace Pad.Warehouse.Tests;

public class EmployeeServiceTests
{
    private static EmployeeService NewService() => new(new InMemoryEmployeeRepository());

    private static Employee Emp(int id, string name = "Ion Popescu", DateTime? updatedAt = null) => new()
    {
        Id = id,
        Name = name,
        Position = "Engineer",
        Department = "R&D",
        UpdatedAt = updatedAt ?? default,
    };

    private static async Task<EmployeeService> WithEmployees(int count)
    {
        var service = NewService();
        for (int i = 1; i <= count; i++)
            await service.PutAsync(Emp(i, $"Angajat {i}"));
        return service;
    }

    // ----- id -----

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Get_IdInvalid_ArunciBadRequest(int id)
    {
        await Assert.ThrowsAsync<BadRequestException>(() => NewService().GetAsync(id));
    }

    [Fact]
    public async Task Get_IdInexistent_ReturneazaNull()
    {
        Assert.Null(await NewService().GetAsync(999));
    }

    // ----- paginare -----

    [Fact]
    public async Task List_FaraParametri_FolosesteLimitulImplicit()
    {
        var service = await WithEmployees(25);

        var page = await service.ListAsync(null, null);

        Assert.Equal(EmployeeService.DefaultLimit, page.Count);
        Assert.Equal(1, page[0].Id);   // ordonat dupa id
    }

    [Fact]
    public async Task List_LimitPesteMaxim_EsteRedusLaMaxim()
    {
        var service = await WithEmployees(150);

        var page = await service.ListAsync(0, 5000);

        Assert.Equal(EmployeeService.MaxLimit, page.Count);
    }

    [Theory]
    [InlineData(0, 10, 10)]    // prima pagina
    [InlineData(20, 10, 5)]    // ultima pagina, incompleta
    [InlineData(25, 10, 0)]    // exact la capat
    [InlineData(100, 10, 0)]   // dupa capat
    public async Task List_LaMargini_ReturneazaNumarulAsteptat(int offset, int limit, int expected)
    {
        var service = await WithEmployees(25);

        var page = await service.ListAsync(offset, limit);

        Assert.Equal(expected, page.Count);
    }

    [Fact]
    public async Task List_OffsetNegativ_ArunciBadRequest()
    {
        await Assert.ThrowsAsync<BadRequestException>(() => NewService().ListAsync(-1, 10));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task List_LimitSubUnu_ArunciBadRequest(int limit)
    {
        await Assert.ThrowsAsync<BadRequestException>(() => NewService().ListAsync(0, limit));
    }

    // ----- PUT -----

    [Fact]
    public async Task Put_AcelasiAngajatDeDouaOri_PrimaDataCreat_ApoiNu_FaraDublura()
    {
        var service = NewService();

        var first = await service.PutAsync(Emp(1));
        var second = await service.PutAsync(Emp(1));

        Assert.True(first.Created);
        Assert.False(second.Created);
        Assert.Single(await service.ListAsync(0, 100));
    }

    [Fact]
    public async Task Put_FaraNume_ArunciBadRequest()
    {
        await Assert.ThrowsAsync<BadRequestException>(() => NewService().PutAsync(Emp(1, name: " ")));
    }

    [Fact]
    public async Task Put_IdInvalid_ArunciBadRequest()
    {
        await Assert.ThrowsAsync<BadRequestException>(() => NewService().PutAsync(Emp(0)));
    }

    [Fact]
    public async Task Put_FaraUpdatedAt_FolosesteMomentulPrimirii()
    {
        var before = DateTime.UtcNow.AddSeconds(-1);

        var (stored, _) = await NewService().PutAsync(Emp(1));

        Assert.True(stored.UpdatedAt > before);
        Assert.Equal(DateTimeKind.Utc, stored.UpdatedAt.Kind);
    }

    [Fact]
    public async Task Put_ScriereMaiVeche_NuSuprascrieCeaMaiNoua()
    {
        var service = NewService();
        var newer = new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);
        var older = new DateTime(2026, 11, 9, 10, 0, 0, DateTimeKind.Utc);

        await service.PutAsync(Emp(1, "nou", newer));
        var (stored, created) = await service.PutAsync(Emp(1, "vechi", older));   // ajunge in alta ordine

        Assert.False(created);
        Assert.Equal("nou", stored.Name);
        Assert.Equal(newer, (await service.GetAsync(1))!.UpdatedAt);
    }

    // ----- POST -----

    [Fact]
    public async Task Patch_DoarNumele_PastreazaPozitiaSiDepartamentul()
    {
        var service = NewService();
        await service.PutAsync(Emp(1));

        var patched = await service.PatchAsync(1, new EmployeeUpdate { Name = "Nume Nou" });

        Assert.NotNull(patched);
        Assert.Equal("Nume Nou", patched!.Name);
        Assert.Equal("Engineer", patched.Position);
        Assert.Equal("R&D", patched.Department);
    }

    [Fact]
    public async Task Patch_IdInexistent_ReturneazaNull()
    {
        Assert.Null(await NewService().PatchAsync(42, new EmployeeUpdate { Name = "X" }));
    }

    // ----- since -----

    [Fact]
    public async Task ChangedSince_FaraParametru_ArunciBadRequest()
    {
        await Assert.ThrowsAsync<BadRequestException>(() => NewService().ChangedSinceAsync(null));
    }

    [Fact]
    public async Task ChangedSince_ReturneazaDoarCeASchimbatDupaMoment()
    {
        var service = NewService();
        await service.PutAsync(Emp(1, updatedAt: new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc)));
        await service.PutAsync(Emp(2, updatedAt: new DateTime(2026, 11, 5, 0, 0, 0, DateTimeKind.Utc)));

        var changed = await service.ChangedSinceAsync(new DateTimeOffset(2026, 11, 3, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(new[] { 2 }, changed.Select(e => e.Id).ToArray());
    }

    // ----- since: antetul If-Modified-Since (ca in enunt) -----

    private static async Task<EmployeeService> WithEmployeesOn(params (int id, DateTime when)[] items)
    {
        var service = NewService();
        foreach (var (id, when) in items)
            await service.PutAsync(Emp(id, updatedAt: when));
        return service;
    }

    [Fact]
    public async Task ChangedSince_AntetHttpDate_ReturneazaDoarCeASchimbatDupaMoment()
    {
        var service = await WithEmployeesOn(
            (1, new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc)),
            (2, new DateTime(2026, 11, 5, 0, 0, 0, DateTimeKind.Utc)));

        var changed = await service.ChangedSinceAsync("Wed, 04 Nov 2026 00:00:00 GMT", null);

        Assert.Equal(new[] { 2 }, changed.Select(e => e.Id).ToArray());
    }

    [Fact]
    public async Task ChangedSince_ExempluDinEnunt_ZiuaSaptamaniiGresitaEsteAcceptata()
    {
        // "Sat, 29 Oct 2014 19:43:31 GMT" este exemplul din enuntul laboratorului, dar 29.10.2014 a fost miercuri.
        var service = await WithEmployeesOn(
            (1, new DateTime(2014, 10, 1, 0, 0, 0, DateTimeKind.Utc)),
            (2, new DateTime(2014, 11, 5, 0, 0, 0, DateTimeKind.Utc)));

        var changed = await service.ChangedSinceAsync("Sat, 29 Oct 2014 19:43:31 GMT", null);

        Assert.Equal(new[] { 2 }, changed.Select(e => e.Id).ToArray());
    }

    [Fact]
    public async Task ChangedSince_AntetulPrimeazaFataDeParametru()
    {
        var service = await WithEmployeesOn(
            (1, new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc)),
            (2, new DateTime(2026, 11, 5, 0, 0, 0, DateTimeKind.Utc)));

        // Daca ar castiga parametrul (10 noiembrie), lista ar fi goala.
        var changed = await service.ChangedSinceAsync(
            "Wed, 04 Nov 2026 00:00:00 GMT",
            new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(new[] { 2 }, changed.Select(e => e.Id).ToArray());
    }

    [Fact]
    public async Task ChangedSince_FaraAntet_FolosesteParametrulSince()
    {
        var service = await WithEmployeesOn(
            (1, new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc)),
            (2, new DateTime(2026, 11, 5, 0, 0, 0, DateTimeKind.Utc)));

        var changed = await service.ChangedSinceAsync(null, new DateTimeOffset(2026, 11, 3, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(new[] { 2 }, changed.Select(e => e.Id).ToArray());
    }

    [Fact]
    public async Task ChangedSince_AntetIso8601_EsteAcceptat()
    {
        var service = await WithEmployeesOn(
            (1, new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc)),
            (2, new DateTime(2026, 11, 5, 0, 0, 0, DateTimeKind.Utc)));

        var changed = await service.ChangedSinceAsync("2026-11-03T00:00:00Z", null);

        Assert.Equal(new[] { 2 }, changed.Select(e => e.Id).ToArray());
    }

    [Theory]
    [InlineData("nu e o data")]
    [InlineData("31 Feb 2026 00:00:00 GMT")]
    public async Task ChangedSince_AntetInvalid_ArunciBadRequest(string header)
    {
        await Assert.ThrowsAsync<BadRequestException>(() => NewService().ChangedSinceAsync(header, null));
    }

    [Fact]
    public async Task ChangedSince_FaraAntetSiFaraParametru_ArunciBadRequest()
    {
        await Assert.ThrowsAsync<BadRequestException>(() => NewService().ChangedSinceAsync(null, null));
    }

    // ----- concurenta pe repository-ul din memorie -----

    [Fact]
    public async Task Put_50DeCereriInParalel_ToateSuntSalvate()
    {
        var service = NewService();

        await Task.WhenAll(Enumerable.Range(1, 50).Select(i => Task.Run(() => service.PutAsync(Emp(i)))));

        Assert.Equal(50, (await service.ListAsync(0, 100)).Count);
    }
}
