using System.Collections.Concurrent;

namespace SmartShift.Core.Logging;

/// <summary>
/// Process-local structured diagnostic logger. Does not send data over the network.
/// </summary>
public sealed class DiagnosticLogger
{
    private readonly ConcurrentQueue<DiagnosticEntry> _entries = new();
    private readonly int _capacity;

    public DiagnosticLogger(int capacity = 500)
    {
        _capacity = Math.Max(50, capacity);
    }

    public event Action<DiagnosticEntry>? EntryAdded;

    public IReadOnlyList<DiagnosticEntry> Snapshot() => _entries.ToArray();

    public void Info(string category, string message, IReadOnlyDictionary<string, string>? data = null)
        => Write("INFO", category, message, data);

    public void Warn(string category, string message, IReadOnlyDictionary<string, string>? data = null)
        => Write("WARN", category, message, data);

    public void Error(string category, string message, Exception? exception = null, IReadOnlyDictionary<string, string>? data = null)
    {
        var payload = data is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(data);

        if (exception is not null)
        {
            payload["exceptionType"] = exception.GetType().FullName ?? exception.GetType().Name;
            payload["exception"] = exception.ToString();
        }

        Write("ERROR", category, message, payload);
    }

    private void Write(string level, string category, string message, IReadOnlyDictionary<string, string>? data)
    {
        var entry = new DiagnosticEntry(
            TimestampUtc: DateTimeOffset.UtcNow,
            Level: level,
            Category: category,
            Message: message,
            Data: data is null ? new Dictionary<string, string>() : new Dictionary<string, string>(data));

        _entries.Enqueue(entry);
        while (_entries.Count > _capacity && _entries.TryDequeue(out _))
        {
        }

        System.Diagnostics.Trace.WriteLine(entry.ToString());
        EntryAdded?.Invoke(entry);
    }
}

public sealed record DiagnosticEntry(
    DateTimeOffset TimestampUtc,
    string Level,
    string Category,
    string Message,
    IReadOnlyDictionary<string, string> Data)
{
    public override string ToString()
    {
        var extras = Data.Count == 0
            ? string.Empty
            : " " + string.Join(" ", Data.Select(kv => $"{kv.Key}={kv.Value}"));
        return $"{TimestampUtc:O} [{Level}] {Category}: {Message}{extras}";
    }
}
