namespace Pad.Warehouse.Startup;

/// <summary>
/// X-Correlation-Id vine de la client/proxy si leaga liniile de log ale aceleiasi cereri intre componente.
/// Valoarea este controlata de client, deci o curatam inainte s-o scriem in log sau s-o trimitem inapoi:
/// altfel un client ar putea strecura caractere de control si ar falsifica linii de log.
/// </summary>
public static class CorrelationId
{
    public const string Header = "X-Correlation-Id";
    public const int MaxLength = 64;

    /// <summary>Pastreaza doar litere, cifre, '.', '_' si '-', maximum MaxLength. Null daca nu ramane nimic.</summary>
    public static string? Clean(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var kept = new System.Text.StringBuilder();
        foreach (char c in raw)
        {
            if (kept.Length == MaxLength)
                break;
            if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c is '.' or '_' or '-')
                kept.Append(c);
        }

        return kept.Length == 0 ? null : kept.ToString();
    }
}
