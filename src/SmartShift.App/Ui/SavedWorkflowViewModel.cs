using System.ComponentModel;
using System.Runtime.CompilerServices;
using SmartShift.Core.History;
using SmartShift.Core.Workflows;

namespace SmartShift.App.Ui;

public sealed class SavedWorkflowViewModel : INotifyPropertyChanged
{
    private readonly SavedWorkflow _workflow;

    public SavedWorkflowViewModel(SavedWorkflow workflow)
    {
        _workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
    }

    public SavedWorkflow Workflow => _workflow;

    public string Id => _workflow.Id;

    public string Name => _workflow.Name;

    public string Description => _workflow.Description;

    public OperationCategory Category => _workflow.Category;

    public string ActionId => _workflow.ActionId;

    public bool IsBuiltIn => _workflow.IsBuiltIn;

    public string CategoryBadgeText => _workflow.Category.ToString();

    public string OptionsSummary
    {
        get
        {
            if (_workflow.Options.Count == 0) return "Default options";
            return string.Join(", ", _workflow.Options.Select(kv => $"{kv.Key}: {kv.Value}"));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
