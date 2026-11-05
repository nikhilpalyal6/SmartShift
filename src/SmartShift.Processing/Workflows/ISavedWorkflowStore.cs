using SmartShift.Core.Workflows;

namespace SmartShift.Processing.Workflows;

public interface ISavedWorkflowStore
{
    string StoragePath { get; }

    IReadOnlyList<SavedWorkflow> GetAll();

    SavedWorkflow? FindById(string id);

    void Save(SavedWorkflow workflow);

    SavedWorkflow Duplicate(string id, string newName);

    bool Delete(string id);
}
