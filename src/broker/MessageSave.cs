using System.Text;
using Pad.Common;

namespace Pad.Broker;

public class MessageSave : IDisposable
{
	// După câte mesaje rescriem fișierul
	private const int CompactEveryDone = 500;

	private sealed class Entry
	{
		public required Message Message { get; init; }
		public required string Json { get; init; }
		public required long Sequence { get; init; }
		public List<string> Acks { get; } = new();
	}

	private readonly string _filePath;
	private readonly Logger _log;
	private readonly object _lock = new();
	private readonly Dictionary<string, Entry> _live = new();
	private long _sequence;
	private int _doneSinceCompact;

	private FileStream? _fileStream;
	private StreamWriter? _writer;
	public MessageSave(string filePath, Logger log)
	{
		_filePath = filePath;
		_log = log;

		string? dir = Path.GetDirectoryName(filePath);
		if (!string.IsNullOrEmpty(dir))
			Directory.CreateDirectory(dir);

		lock (_lock)
		{
			Load();
			Rewrite();
			Open();
		}

	}

	// Încarcă mesajele din fișierul de salvare
	public List<QueuedMessage> Recover()
	{
		lock (_lock)
		{
			var result = new List<QueuedMessage>();
			foreach (Entry e in _live.Values.OrderBy(e => e.Sequence))
			{
				var queued = new QueuedMessage { Message = e.Message };
				foreach (string receiver in e.Acks)
					queued.RestoreAck(receiver);
				result.Add(queued);
			}
			return result;
		}
	}

	public void Enqueued(Message message)
	{
		string json = Json.Serialize(message);
		lock (_lock)
		{
			_live[message.MessageId] = new Entry { Message = message, Json = json, Sequence = _sequence++ };
			Append("ENQUEUE: " + json, fsync: true);
		}
	}

	public void Acked(string messageId, string receiver)
	{
		lock (_lock)
		{
			if (!_live.TryGetValue(messageId, out Entry? entry))
				return;
			if (!entry.Acks.Contains(receiver))
				entry.Acks.Add(receiver);
			Append($"ACK: {messageId} {receiver}", fsync: true);
		}
	}

	public void Done(string messageId)
	{
		lock (_lock)
		{
			if (!_live.Remove(messageId))
				return;
			Append($"DONE: {messageId}", fsync: true);
			if (++_doneSinceCompact >= CompactEveryDone)
			{
				Close();
				Rewrite();
				Open();
			}
		}
	}

	public void Dispose()
	{
		lock (_lock)
		{
			Close();
		}
	}

	private void Append(string line, bool fsync)
	{
		_writer!.WriteLine(line + "\n");
		_writer!.Flush();
		if (fsync)
			_fileStream!.Flush(true);
     }

	private void Open()
	{
		_fileStream = new FileStream(_filePath, FileMode.Append, FileAccess.Write, FileShare.Read);
		_writer = new StreamWriter(_fileStream, new UTF8Encoding(false));
     }

	private void Close()
	{
		_writer?.Dispose();
		_writer = null;
		_fileStream = null;
     }

	private void Load()
	{
		if (!File.Exists(_filePath))
			return;

		string[] lines = File.ReadAllText(_filePath, Encoding.UTF8).Split('\n');
		int skipped = 0;

		for (int i = 0; i < lines.Length - 1; i++)
		{
			string line = lines[i].TrimEnd('\r');

			if (line.StartsWith("ENQUEUE: "))
			{
				string json = line.Substring(9);
				Message? message = Json.TryDeserialize<Message>(json);
				if (message == null || string.IsNullOrWhiteSpace(message.MessageId))
				{
					skipped++;
					continue;
				}
				_live[message.MessageId] = new Entry { Message = message, Json = json, Sequence = _sequence++ };
			}
			else if (line.StartsWith("ACK: "))
			{
				string[] parts = line.Substring(5).Split(' ', 2);
				if (parts.Length != 2)
				{
					skipped++;
					continue;
				}
				string messageId = parts[0];
				string receiver = parts[1];
				if (_live.TryGetValue(messageId, out Entry? entry) && !entry.Acks.Contains(receiver))
					entry.Acks.Add(receiver);
			}
			else if (line.StartsWith("DONE: "))
			{
				string messageId = line.Substring(6);
				_live.Remove(messageId);
			}
			else
			{
				skipped++;
               }
          }
		_log.Info($"Loaded {_live.Count} messages from save file, skipped {skipped} invalid lines.");
     }

	private void Rewrite()
	{
		string tempFile = _filePath + ".tmp";
		using (var fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None))
		using (var writer = new StreamWriter(fs, new UTF8Encoding(false)))
		{
			foreach (Entry e in _live.Values.OrderBy(e => e.Sequence))
			{
				writer.WriteLine("ENQUEUE: " + e.Json);
				foreach (string receiver in e.Acks)
					writer.WriteLine($"ACK: {e.Message.MessageId} {receiver}");
			}
			writer.Flush();
			fs.Flush(true);
		}
		File.Move(tempFile, _filePath, overwrite: true);
		_doneSinceCompact = 0;
     }
}
