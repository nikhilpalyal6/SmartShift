using System.Text.Json;
using SmartShift.Core.History;

namespace SmartShift.Processing.History;

/// <summary>
/// Thread-safe, persistent file-based store for processing operations history.
/// Supports atomic writes, query filtering, and graceful recovery from corrupted files.
/// </summary>
public sealed class ProcessingHistoryStore : IProcessingHistoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _storagePath;
    private readonly object _lock = new();

    public ProcessingHistoryStore(string? storagePath = null)
    {
        _storagePath = storagePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SmartShift",
            "processing_history.json");
    }

    public string StoragePath => _storagePath;

    public event Action<OperationRecord>? OperationRecorded;
    public event Action? HistoryCleared;

    public IReadOnlyList<OperationRecord> GetAll()
    {
        lock (_lock)
        {
            var records = LoadInternal();
            // Return ordered newest first
            return records.OrderByDescending(r => r.StartedAtUtc).ToList();
        }
    }

    public IReadOnlyList<OperationRecord> Query(OperationFilterCriteria criteria)
    {
        ArgumentNullException.ThrowIfNull(criteria);

        lock (_lock)
        {
            var records = LoadInternal();
            return records
                .Where(criteria.Matches)
                .OrderByDescending(r => r.StartedAtUtc)
                .ToList();
        }
    }

    public OperationRecord? FindById(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;

        lock (_lock)
        {
            var records = LoadInternal();
            return records.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
        }
    }

    public void Record(OperationRecord record) => RecordOperation(record);

    public void RecordOperation(OperationRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        lock (_lock)
        {
            var records = LoadInternal();

            // Prevent duplicate records caused by multiple callbacks
            var existingIndex = records.FindIndex(r => string.Equals(r.Id, record.Id, StringComparison.OrdinalIgnoreCase));
            if (existingIndex >= 0)
            {
                records[existingIndex] = record;
            }
            else
            {
                records.Add(record);
            }

            // Cap history to 5,000 entries to prevent unbounded memory growth
            if (records.Count > 5000)
            {
                records = records
                    .OrderByDescending(r => r.StartedAtUtc)
                    .Take(5000)
                    .ToList();
            }

            PersistInternal(records);
        }

        OperationRecorded?.Invoke(record);
    }

    public bool Remove(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;

        lock (_lock)
        {
            var records = LoadInternal();
            var removed = records.RemoveAll(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed)
            {
                PersistInternal(records);
            }

            return removed;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            PersistInternal([]);
        }

        HistoryCleared?.Invoke();
    }

    private List<OperationRecord> LoadInternal()
    {
        if (!File.Exists(_storagePath))
        {
            return [];
        }

        try
        {
            var json = File.ReadAllText(_storagePath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return [];
            }

            var list = JsonSerializer.Deserialize<List<OperationRecord>>(json, JsonOptions);
            return list ?? [];
        }
        catch
        {
            // Corrupt file recovery: backup corrupt file and return fresh empty list
            TryBackupCorruptFile();
            return [];
        }
    }

    private void PersistInternal(List<OperationRecord> records)
    {
        try
        {
            var dir = Path.GetDirectoryName(_storagePath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(records, JsonOptions);
            var tempPath = _storagePath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _storagePath, overwrite: true);
        }
        catch
        {
            // Best effort persistence
        }
    }

    private void TryBackupCorruptFile()
    {
        try
        {
            if (File.Exists(_storagePath))
            {
                var backupPath = _storagePath + $".corrupt_{DateTime.UtcNow:yyyyMMddHHmmss}";
                File.Move(_storagePath, backupPath, overwrite: true);
            }
        }
        catch
        {
            // Ignore backup failures
        }
    }
}
