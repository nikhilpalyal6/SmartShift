using SmartShift.Core.History;

namespace SmartShift.Processing.History;

public interface IProcessingHistoryStore
{
    string StoragePath { get; }

    event Action<OperationRecord>? OperationRecorded;
    event Action? HistoryCleared;

    IReadOnlyList<OperationRecord> GetAll();

    IReadOnlyList<OperationRecord> Query(OperationFilterCriteria criteria);

    OperationRecord? FindById(string id);

    void RecordOperation(OperationRecord record);

    void Record(OperationRecord record) => RecordOperation(record);

    bool Remove(string id);

    void Clear();
}
