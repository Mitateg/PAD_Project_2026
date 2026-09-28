using Pad.Common;

namespace Pad.Receiver;

/// <summary>
/// Proceseaza un mesaj primit: il afiseaza in consola si il scrie in logs/processed-{name}.log.
/// Broker-ul livreaza "at-least-once", deci duplicatele sunt normale - le ignoram dupa messageId.
/// </summary>
public class MessageProcessor
{
    private readonly string _processedFilePath;
    private readonly bool _deduplicate;
    private readonly Logger _log;

    // Reincarcate din fisier la pornire, ca deduplicarea sa supravietuiasca unui restart.
    private readonly HashSet<string> _processedIds = new();

    /// <summary>Apelat dupa afisarea unui mesaj; Program il foloseste ca sa reafiseze meniul.</summary>
    public Action? AfterMessageShown { get; set; }

    /// <summary>Cate mesaje unice am procesat pana acum. Afisat in meniu.</summary>
    public int ProcessedCount { get; private set; }

    public MessageProcessor(string receiverName, bool deduplicate, Logger log)
    {
        _deduplicate = deduplicate;
        _log = log;

        Directory.CreateDirectory("logs");
        _processedFilePath = Path.Combine("logs", $"processed-{receiverName}.log");

        if (_deduplicate && File.Exists(_processedFilePath))
        {
            foreach (string line in File.ReadLines(_processedFilePath))
                _processedIds.Add(line.Split(" | ")[0]);

            ProcessedCount = _processedIds.Count;
            _log.Info("dedup_loaded", result: $"{_processedIds.Count} ids from {_processedFilePath}");
        }
    }

    /// <returns>false daca era duplicat si a fost ignorat, true daca a fost procesat acum.</returns>
    public bool Process(Message message, string rawJson, bool isPrivate)
    {
        if (_deduplicate && _processedIds.Contains(message.MessageId))
        {
            Console.WriteLine();
            Console.WriteLine($">>> DUPLICAT ignorat (id {message.MessageId}, tip {message.MessageType})");
            _log.Warn("duplicate_skipped", message.CorrelationId, message.MessageId, message.MessageType, "already processed");
            AfterMessageShown?.Invoke();
            return false;
        }

        string kind = isPrivate ? "PRIVAT, doar pentru tine" : $"public, tip {message.MessageType}";
        Console.WriteLine();
        Console.WriteLine("------------------------------------------------------------");
        Console.WriteLine($">>> MESAJ NOU ({kind}) [{DateTime.Now:HH:mm:ss}]");
        Console.WriteLine(rawJson);
        Console.WriteLine("------------------------------------------------------------");

        File.AppendAllText(_processedFilePath, $"{message.MessageId} | {rawJson}{Environment.NewLine}");

        _processedIds.Add(message.MessageId);
        ProcessedCount++;
        _log.Info("message_processed", message.CorrelationId, message.MessageId, message.MessageType);

        // Reafisam meniul abia acum, ca sa apara contorul deja actualizat.
        AfterMessageShown?.Invoke();
        return true;
    }
}