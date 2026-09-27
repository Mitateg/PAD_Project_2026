using Pad.Common;
using Xunit;

namespace Pad.Contract.Tests;

public class ExamplesTests
{
    private static string ExamplesDir =>
        Path.Combine(AppContext.BaseDirectory, "examples");

    public static IEnumerable<object[]> ValidFiles() =>
        Directory.GetFiles(ExamplesDir, "valid-*.json").Select(f => new object[] { Path.GetFileName(f) });

    public static IEnumerable<object[]> InvalidFiles() =>
        Directory.GetFiles(ExamplesDir, "invalid-*.json").Select(f => new object[] { Path.GetFileName(f) });

    [Theory]
    [MemberData(nameof(ValidFiles))]
    public void ExempleleValide_TrecValidarea(string fileName)
    {
        string line = File.ReadAllText(Path.Combine(ExamplesDir, fileName)).Trim();

        bool ok = MessageValidator.TryParse(line, out _, out var reason);

        Assert.True(ok, $"{fileName} ar fi trebuit sa fie valid, dar: {reason}");
    }

    [Theory]
    [MemberData(nameof(InvalidFiles))]
    public void ExempleleInvalide_PicaValidareaCuMotivulDinNume(string fileName)
    {
        // Numele fisierului: invalid-<motiv>.json, de ex. invalid-missing_field-messageType.json
        // => motiv asteptat: missing_field:messageType
        string expectedReason = Path.GetFileNameWithoutExtension(fileName)
            .Substring("invalid-".Length)
            .Replace("-", ":");

        string line = File.ReadAllText(Path.Combine(ExamplesDir, fileName)).Trim();

        bool ok = MessageValidator.TryParse(line, out _, out var reason);

        Assert.False(ok, $"{fileName} ar fi trebuit sa fie invalid");
        Assert.Equal(expectedReason, reason);
    }
}
