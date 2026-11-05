using System.Text.Json;
using SmartShift.Core.Workflows;

namespace SmartShift.Processing.Workflows;

/// <summary>
/// Thread-safe, persistent file-based store for reusable saved workflows with built-in fallbacks.
/// </summary>
public sealed class SavedWorkflowStore : ISavedWorkflowStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _storagePath;
    private readonly object _lock = new();

    public SavedWorkflowStore(string? storagePath = null)
    {
        _storagePath = storagePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SmartShift",
            "saved_workflows.json");
    }

    public string StoragePath => _storagePath;

    public IReadOnlyList<SavedWorkflow> GetAll()
    {
        lock (_lock)
        {
            var builtIns = SavedWorkflow.GetBuiltInWorkflows();
            var userWorkflows = LoadUserWorkflows();

            var combined = new List<SavedWorkflow>(builtIns);
            foreach (var user in userWorkflows)
            {
                if (!combined.Any(w => string.Equals(w.Id, user.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    combined.Add(user);
                }
            }

            return combined.OrderBy(w => w.Name).ToList();
        }
    }

    public SavedWorkflow? FindById(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        return GetAll().FirstOrDefault(w => string.Equals(w.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    public void Save(SavedWorkflow workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);

        var errors = workflow.Validate();
        if (errors.Count > 0)
        {
            throw new ArgumentException($"Invalid workflow: {string.Join(" ", errors)}");
        }

        lock (_lock)
        {
            var userWorkflows = LoadUserWorkflows();
            var existingIndex = userWorkflows.FindIndex(w => string.Equals(w.Id, workflow.Id, StringComparison.OrdinalIgnoreCase));

            var workflowToSave = workflow with
            {
                IsBuiltIn = false,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };

            if (existingIndex >= 0)
            {
                userWorkflows[existingIndex] = workflowToSave;
            }
            else
            {
                userWorkflows.Add(workflowToSave);
            }

            PersistUserWorkflows(userWorkflows);
        }
    }

    public SavedWorkflow Duplicate(string id, string newName)
    {
        var existing = FindById(id) ?? throw new InvalidOperationException($"Workflow '{id}' not found.");
        var name = string.IsNullOrWhiteSpace(newName) ? $"{existing.Name} (Copy)" : newName.Trim();

        var duplicated = new SavedWorkflow(
            Id: Guid.NewGuid().ToString("N"),
            Name: name,
            Description: existing.Description,
            Category: existing.Category,
            ActionId: existing.ActionId,
            Options: new Dictionary<string, string>(existing.Options),
            CreatedAtUtc: DateTimeOffset.UtcNow,
            UpdatedAtUtc: DateTimeOffset.UtcNow,
            IsBuiltIn: false);

        Save(duplicated);
        return duplicated;
    }

    public bool Delete(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;

        lock (_lock)
        {
            // Built-in workflows cannot be deleted
            if (SavedWorkflow.GetBuiltInWorkflows().Any(b => string.Equals(b.Id, id, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            var userWorkflows = LoadUserWorkflows();
            var removed = userWorkflows.RemoveAll(w => string.Equals(w.Id, id, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed)
            {
                PersistUserWorkflows(userWorkflows);
            }

            return removed;
        }
    }

    private List<SavedWorkflow> LoadUserWorkflows()
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

            var items = JsonSerializer.Deserialize<List<SavedWorkflow>>(json, JsonOptions);
            return items?.Where(w => w.Validate().Count == 0).ToList() ?? [];
        }
        catch
        {
            // Corrupt file recovery: return empty user list so built-ins continue to work
            return [];
        }
    }

    private void PersistUserWorkflows(List<SavedWorkflow> workflows)
    {
        try
        {
            var dir = Path.GetDirectoryName(_storagePath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(workflows, JsonOptions);
            var tempPath = _storagePath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _storagePath, overwrite: true);
        }
        catch
        {
            // Best effort persistence
        }
    }
}
