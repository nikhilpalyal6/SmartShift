using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SmartShift.App.Ui;
using SmartShift.Core.Actions;
using SmartShift.Core.Classification;
using SmartShift.Core.History;
using SmartShift.Core.Workflows;
using SmartShift.Windows.Shell;

namespace SmartShift.App;

public partial class MainWindow
{
    private readonly ObservableCollection<HistoryEntryViewModel> _historyEntries = [];
    private readonly ObservableCollection<SavedWorkflowViewModel> _workflowEntries = [];
    private bool _isInitializingHistory;

    private void InitializeHistoryStudio()
    {
        if (HistoryListBox is null) return;
        _isInitializingHistory = true;

        try
        {
            HistoryListBox.ItemsSource = _historyEntries;
            if (WorkflowsListBox is not null)
            {
                WorkflowsListBox.ItemsSource = _workflowEntries;
            }

            // Populate status filter combo
            if (HistoryStatusFilterCombo is not null && HistoryStatusFilterCombo.Items.Count == 0)
            {
                HistoryStatusFilterCombo.Items.Add(new ComboBoxItem { Content = "All Status", Tag = null });
                HistoryStatusFilterCombo.Items.Add(new ComboBoxItem { Content = "✓ Completed", Tag = OperationStatus.Completed });
                HistoryStatusFilterCombo.Items.Add(new ComboBoxItem { Content = "✕ Failed", Tag = OperationStatus.Failed });
                HistoryStatusFilterCombo.Items.Add(new ComboBoxItem { Content = "⊘ Cancelled", Tag = OperationStatus.Cancelled });
                HistoryStatusFilterCombo.SelectedIndex = 0;
            }

            // Populate category filter combo
            if (HistoryCategoryFilterCombo is not null && HistoryCategoryFilterCombo.Items.Count == 0)
            {
                HistoryCategoryFilterCombo.Items.Add(new ComboBoxItem { Content = "All Categories", Tag = OperationCategory.All });
                HistoryCategoryFilterCombo.Items.Add(new ComboBoxItem { Content = "Image", Tag = OperationCategory.Image });
                HistoryCategoryFilterCombo.Items.Add(new ComboBoxItem { Content = "PDF", Tag = OperationCategory.Pdf });
                HistoryCategoryFilterCombo.Items.Add(new ComboBoxItem { Content = "Document", Tag = OperationCategory.Document });
                HistoryCategoryFilterCombo.SelectedIndex = 0;
            }

            // Subscribe to history updates
            App.Services.HistoryStore.OperationRecorded += OnOperationRecorded;
            App.Services.HistoryStore.HistoryCleared += OnHistoryCleared;

            RefreshHistoryList();
            RefreshWorkflowsList();
        }
        finally
        {
            _isInitializingHistory = false;
        }
    }

    private void OnOperationRecorded(OperationRecord record)
    {
        Dispatcher.InvokeAsync(() =>
        {
            RefreshHistoryList();
        });
    }

    private void OnHistoryCleared()
    {
        Dispatcher.InvokeAsync(() =>
        {
            _historyEntries.Clear();
            UpdateHistoryHeader();
            if (HistoryInspectorCard is not null) HistoryInspectorCard.Visibility = Visibility.Collapsed;
            if (HistoryEmptyPlaceholder is not null) HistoryEmptyPlaceholder.Visibility = Visibility.Visible;
        });
    }

    private void RefreshHistoryList()
    {
        var criteria = BuildFilterCriteria();
        var records = App.Services.HistoryStore.Query(criteria);

        _historyEntries.Clear();
        foreach (var r in records)
        {
            _historyEntries.Add(new HistoryEntryViewModel(r));
        }

        UpdateHistoryHeader();

        if (HistoryEmptyPlaceholder is not null)
        {
            HistoryEmptyPlaceholder.Visibility = _historyEntries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        if (HistoryListBox is not null && HistoryListBox.SelectedItem is null && _historyEntries.Count > 0)
        {
            HistoryListBox.SelectedIndex = 0;
        }
        else if (_historyEntries.Count == 0 && HistoryInspectorCard is not null)
        {
            HistoryInspectorCard.Visibility = Visibility.Collapsed;
        }
    }

    private void RefreshWorkflowsList()
    {
        var workflows = App.Services.WorkflowStore.GetAll();
        _workflowEntries.Clear();
        foreach (var w in workflows)
        {
            _workflowEntries.Add(new SavedWorkflowViewModel(w));
        }

        if (WorkflowsListBox is not null && WorkflowsListBox.SelectedItem is null && _workflowEntries.Count > 0)
        {
            WorkflowsListBox.SelectedIndex = 0;
        }
    }

    private OperationFilterCriteria BuildFilterCriteria()
    {
        string? search = HistorySearchBox?.Text?.Trim();
        OperationStatus? status = null;
        if (HistoryStatusFilterCombo?.SelectedItem is ComboBoxItem { Tag: OperationStatus s })
        {
            status = s;
        }

        var category = OperationCategory.All;
        if (HistoryCategoryFilterCombo?.SelectedItem is ComboBoxItem { Tag: OperationCategory c })
        {
            category = c;
        }

        return new OperationFilterCriteria(
            SearchText: search,
            Status: status,
            Category: category);
    }

    private void UpdateHistoryHeader()
    {
        if (HistoryCountBadge is not null)
        {
            int total = App.Services.HistoryStore.GetAll().Count;
            int filtered = _historyEntries.Count;
            HistoryCountBadge.Text = total == filtered
                ? $"{total} operation{(total == 1 ? "" : "s")}"
                : $"{filtered} of {total} operations";
        }
    }

    private void HistorySearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isInitializingHistory) return;
        RefreshHistoryList();
    }

    private void HistoryFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializingHistory) return;
        RefreshHistoryList();
    }

    private void HistoryListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HistoryListBox?.SelectedItem is HistoryEntryViewModel item)
        {
            item.RefreshState();
            if (HistoryInspectorCard is not null)
            {
                HistoryInspectorCard.Visibility = Visibility.Visible;
                HistoryInspectorActionName.Text = item.ActionName;
                HistoryInspectorStatusText.Text = item.StatusBadgeText;
                HistoryInspectorStatusText.Foreground = item.StatusBrush;
                HistoryInspectorTimestamp.Text = item.FormattedTimestamp;
                HistoryInspectorDuration.Text = item.FormattedDuration;
                HistoryInspectorInputPath.Text = item.PrimaryInputFileName;
                HistoryInspectorOutputPath.Text = item.PrimaryOutputFileName;
                HistoryInspectorOutputStatus.Text = item.OutputAvailabilityText;
                HistoryInspectorOutputStatus.Foreground = item.OutputAvailabilityBrush;
                HistoryInspectorSize.Text = item.FormattedOutputSize;

                // Failure details & retry button
                if (item.Record.IsFailed && !string.IsNullOrWhiteSpace(item.FailureReason))
                {
                    HistoryInspectorErrorBlock.Visibility = Visibility.Visible;
                    HistoryInspectorErrorText.Text = item.FailureReason;
                    HistoryInspectorRetryBtn.Visibility = item.CanRetry ? Visibility.Visible : Visibility.Collapsed;
                }
                else
                {
                    HistoryInspectorErrorBlock.Visibility = Visibility.Collapsed;
                    HistoryInspectorRetryBtn.Visibility = Visibility.Collapsed;
                }

                // Output action buttons
                HistoryInspectorOpenBtn.IsEnabled = item.IsOutputAvailable;
                HistoryInspectorRevealBtn.IsEnabled = item.IsOutputAvailable;
                HistoryInspectorCopyBtn.IsEnabled = item.HasOutput;
            }
        }
        else if (HistoryInspectorCard is not null)
        {
            HistoryInspectorCard.Visibility = Visibility.Collapsed;
        }
    }

    private void HistoryItemOpen_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryListBox?.SelectedItem is HistoryEntryViewModel item)
        {
            item.OpenOutput();
        }
    }

    private void HistoryItemReveal_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryListBox?.SelectedItem is HistoryEntryViewModel item)
        {
            item.RevealInFolder();
        }
    }

    private void HistoryItemCopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryListBox?.SelectedItem is HistoryEntryViewModel item)
        {
            item.CopyOutputPath();
            ShowBanner("Output file path copied to clipboard.", error: false);
        }
    }

    private async void HistoryItemRetry_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryListBox?.SelectedItem is not HistoryEntryViewModel item || !item.CanRetry) return;

        HistoryInspectorRetryBtn.IsEnabled = false;
        try
        {
            ShowBanner($"Retrying operation '{item.ActionName}'…", error: false);
            var result = await App.Services.RetryService.RetryAsync(item.Record);

            if (result.Succeeded)
            {
                ShowBanner(result.Message, error: false);
                _lastOutputPath = result.OutputPath;
            }
            else
            {
                ShowBanner(result.Message, error: true);
            }

            RefreshHistoryList();
        }
        catch (Exception ex)
        {
            ShowBanner($"Retry failed: {ex.Message}", error: true);
        }
        finally
        {
            HistoryInspectorRetryBtn.IsEnabled = true;
        }
    }

    private void HistoryItemDelete_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryListBox?.SelectedItem is HistoryEntryViewModel item)
        {
            App.Services.HistoryStore.Remove(item.Id);
            RefreshHistoryList();
            ShowBanner("History entry removed. Output and input files on disk were not deleted.", error: false);
        }
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "Clear all operation history?\n\nThis will remove history records only. Input and output files on disk will NEVER be deleted.",
            "Clear Processing History",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            App.Services.HistoryStore.Clear();
            ShowBanner("Processing history cleared. Files on disk remain intact.", error: false);
        }
    }

    private void HistoryTab_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryTabBtn is null || WorkflowsTabBtn is null) return;
        HistoryTabBtn.Style = (Style)FindResource("PrimaryButton");
        WorkflowsTabBtn.Style = (Style)FindResource("SecondaryButton");

        HistoryViewPanel.Visibility = Visibility.Visible;
        WorkflowsViewPanel.Visibility = Visibility.Collapsed;
        RefreshHistoryList();
    }

    private void WorkflowsTab_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryTabBtn is null || WorkflowsTabBtn is null) return;
        HistoryTabBtn.Style = (Style)FindResource("SecondaryButton");
        WorkflowsTabBtn.Style = (Style)FindResource("PrimaryButton");

        HistoryViewPanel.Visibility = Visibility.Collapsed;
        WorkflowsViewPanel.Visibility = Visibility.Visible;
        RefreshWorkflowsList();
    }

    private void WorkflowsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WorkflowsListBox?.SelectedItem is SavedWorkflowViewModel item)
        {
            if (WorkflowInspectorCard is not null)
            {
                WorkflowInspectorCard.Visibility = Visibility.Visible;
                WorkflowInspectorName.Text = item.Name;
                WorkflowInspectorCategory.Text = item.CategoryBadgeText;
                WorkflowInspectorDesc.Text = item.Description;
                WorkflowInspectorOptions.Text = item.OptionsSummary;
                WorkflowInspectorDeleteBtn.IsEnabled = !item.IsBuiltIn;
            }
        }
        else if (WorkflowInspectorCard is not null)
        {
            WorkflowInspectorCard.Visibility = Visibility.Collapsed;
        }
    }

    private async void RunWorkflowOnCurrentFile_Click(object sender, RoutedEventArgs e)
    {
        if (WorkflowsListBox?.SelectedItem is not SavedWorkflowViewModel item) return;

        var targetFile = _selectedFileItem?.File;
        if (targetFile is null || !File.Exists(targetFile.Path))
        {
            ShowBanner("Please select a valid input file in the workspace first.", error: true);
            return;
        }

        try
        {
            ShowBanner($"Executing workflow '{item.Name}' on {targetFile.FileName}…", error: false);
            var result = await App.Services.WorkflowExecution.ExecuteAsync(item.Workflow, targetFile);

            if (result.Succeeded)
            {
                ShowBanner(result.Message, error: false);
                _lastOutputPath = result.OutputPath;
                RefreshHistoryList();
            }
            else
            {
                ShowBanner(result.Message, error: true);
                RefreshHistoryList();
            }
        }
        catch (Exception ex)
        {
            ShowBanner($"Workflow execution error: {ex.Message}", error: true);
        }
    }

    private void DuplicateWorkflow_Click(object sender, RoutedEventArgs e)
    {
        if (WorkflowsListBox?.SelectedItem is not SavedWorkflowViewModel item) return;

        try
        {
            var dup = App.Services.WorkflowStore.Duplicate(item.Id, $"{item.Name} (Copy)");
            RefreshWorkflowsList();
            ShowBanner($"Workflow duplicated as '{dup.Name}'.", error: false);
        }
        catch (Exception ex)
        {
            ShowBanner($"Failed to duplicate workflow: {ex.Message}", error: true);
        }
    }

    private void DeleteWorkflow_Click(object sender, RoutedEventArgs e)
    {
        if (WorkflowsListBox?.SelectedItem is not SavedWorkflowViewModel item) return;
        if (item.IsBuiltIn)
        {
            ShowBanner("Built-in workflows cannot be deleted.", error: true);
            return;
        }

        var confirm = MessageBox.Show(
            $"Delete saved workflow '{item.Name}'?",
            "Delete Workflow",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm == MessageBoxResult.Yes)
        {
            App.Services.WorkflowStore.Delete(item.Id);
            RefreshWorkflowsList();
            ShowBanner($"Workflow '{item.Name}' deleted.", error: false);
        }
    }

    private void SaveCurrentAsWorkflow_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedAction is null)
        {
            ShowBanner("Select and configure an action in the workspace first.", error: true);
            return;
        }

        var values = GatherOptions(_selectedAction.ActionId);
        var category = _selectedFileItem?.File.Format == DetectedFormat.Pdf ? OperationCategory.Pdf : OperationCategory.Image;
        var newWf = new SavedWorkflow(
            Id: Guid.NewGuid().ToString("N"),
            Name: $"My {_selectedAction.Title} Workflow",
            Description: "User-saved custom repeatable workflow",
            Category: category,
            ActionId: _selectedAction.ActionId,
            Options: values,
            CreatedAtUtc: DateTimeOffset.UtcNow,
            UpdatedAtUtc: DateTimeOffset.UtcNow,
            IsBuiltIn: false);

        try
        {
            App.Services.WorkflowStore.Save(newWf);
            RefreshWorkflowsList();
            ShowBanner($"Workflow '{newWf.Name}' saved successfully.", error: false);
        }
        catch (Exception ex)
        {
            ShowBanner($"Failed to save workflow: {ex.Message}", error: true);
        }
    }
}
