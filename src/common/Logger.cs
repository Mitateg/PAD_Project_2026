namespace Pad.Common;

/// <summary>
/// Log cu format comun pentru toate componentele, o linie per eveniment,
/// pe consola si intr-un fisier logs/[componenta].log:
///
/// 2026-09-14T10:00:01Z | INFO | broker | message_received | corr=abc | msg=def | type=Mesaj | ok | 3ms
/// </summary>
public class Logger
{
    private readonly string _component;
    private readonly string _filePath;
    private readonly bool _toConsole;
    private readonly object _lock = new();

    /// <param name="toConsole">false = scrie doar in fisier (util cand consola e folosita pentru un meniu).</param>
    public Logger(string component, bool toConsole = true)
    {
        _component = component;
        _toConsole = toConsole;
        Directory.CreateDirectory("logs");
        _filePath = Path.Combine("logs", component + ".log");
    }

    public void Info(string eventName, string? corr = null, string? msg = null, string? type = null,
                     string result = "ok", TimeSpan? duration = null)
        => Write("INFO", eventName, corr, msg, type, result, duration);

    public void Warn(string eventName, string? corr = null, string? msg = null, string? type = null,
                     string result = "warn", TimeSpan? duration = null)
        => Write("WARN", eventName, corr, msg, type, result, duration);

    public void Error(string eventName, string? corr = null, string? msg = null, string? type = null,
                      string result = "error", TimeSpan? duration = null)
        => Write("ERROR", eventName, corr, msg, type, result, duration);

    private void Write(string level, string eventName, string? corr, string? msg, string? type,
                       string result, TimeSpan? duration)
    {
        string timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        string line = $"{timestamp} | {level} | {_component} | {eventName}"
                    + $" | corr={corr ?? "-"} | msg={msg ?? "-"} | type={type ?? "-"}"
                    + $" | {result}"
                    + (duration.HasValue ? $" | {(int)duration.Value.TotalMilliseconds}ms" : "");

        lock (_lock)
        {
            if (_toConsole)
                Console.WriteLine(line);

            // Deschidem fisierul cu partajare: mai multe procese cu acelasi nume (ex. doi "ion")
            // pot scrie in acelasi log fara sa se blocheze. Un log care nu se poate scrie nu opreste aplicatia.
            try
            {
                using var stream = new FileStream(_filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var writer = new StreamWriter(stream);
                writer.WriteLine(line);
            }
            catch (IOException)
            {
                // fisierul e ocupat exact acum de alt proces; sarim linia, nu e critic
            }
        }
    }
}
