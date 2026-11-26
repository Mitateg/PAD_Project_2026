using Pad.Warehouse.Startup;
using Xunit;

namespace Pad.Warehouse.Tests;

public class CorrelationIdTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<>!?")]
    public void Clean_FaraCeva_Utilizabil_ReturneazaNull(string? raw)
    {
        Assert.Null(CorrelationId.Clean(raw));
    }

    [Theory]
    [InlineData("abc-123", "abc-123")]
    [InlineData("3f2b8c1e-0d4a-4c52-9a7e-1b2c3d4e5f60", "3f2b8c1e-0d4a-4c52-9a7e-1b2c3d4e5f60")]
    [InlineData("req_1.2", "req_1.2")]
    public void Clean_ValoareNormala_RamaNeschimbata(string raw, string expected)
    {
        Assert.Equal(expected, CorrelationId.Clean(raw));
    }

    [Fact]
    public void Clean_ScoateSpatiileLiniileNoiSiBara_CareArPuteaFalsificaOLinieDeLog()
    {
        string? cleaned = CorrelationId.Clean("abc\n2026 | INFO | fake");

        Assert.Equal("abc2026INFOfake", cleaned);
    }

    [Fact]
    public void Clean_TaieLaLungimeaMaxima()
    {
        string? cleaned = CorrelationId.Clean(new string('a', 500));

        Assert.Equal(CorrelationId.MaxLength, cleaned!.Length);
    }
}
