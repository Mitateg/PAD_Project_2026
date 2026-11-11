using System.Globalization;

namespace Pad.Warehouse.Services;

/// <summary>
/// Citirea momentului din antetul If-Modified-Since (data HTTP: "Sat, 29 Oct 2014 19:43:31 GMT").
///
/// Parsarea este voit "blanda":
///  - ziua saptamanii ("Sat,") se ignora. DateTime.Parse o verifica si respinge data daca nu se potriveste,
///    iar chiar exemplul din enuntul laboratorului are o zi gresita (29 octombrie 2014 a fost miercuri);
///  - se accepta si ISO-8601 (2026-11-09T10:00:00Z), ca acelasi cod sa serveasca si pentru ?since=.
/// Data HTTP nu are fractiuni de secunda, deci momentul primit e rotunjit in jos la secunda.
/// </summary>
internal static class HttpDates
{
    private static readonly string[] HttpFormats =
    {
        "d MMM yyyy HH:mm:ss 'GMT'",
        "d MMM yyyy HH:mm:ss",
    };

    public static bool TryParse(string? value, out DateTimeOffset result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        string text = value.Trim();

        // "Sat, 29 Oct 2014 19:43:31 GMT" -> "29 Oct 2014 19:43:31 GMT"
        int comma = text.IndexOf(',');
        string withoutWeekday = comma >= 0 ? text[(comma + 1)..].Trim() : text;

        const DateTimeStyles styles = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;

        if (DateTimeOffset.TryParseExact(withoutWeekday, HttpFormats, CultureInfo.InvariantCulture, styles, out result))
            return true;

        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, styles, out result);
    }
}
