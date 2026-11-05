using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SmartShift.App;
using SmartShift.App.Ui;
using SmartShift.Core.Actions;
using SmartShift.Core.History;
using SmartShift.Core.Workflows;
using Xunit;
using AppClass = SmartShift.App.App;

namespace SmartShift.Windows.Tests;

public sealed class HistoryStudioUiTests
{
    private static void RunOnSta(Action action)
    {
        Exception? threadEx = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = Application.Current ?? new Application();
                System.Threading.SynchronizationContext.SetSynchronizationContext(
                    new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

                var themeUri = new Uri("pack://application:,,,/SmartShift.App;component/Themes/AppTheme.xaml", UriKind.Absolute);
                if (!app.Resources.MergedDictionaries.Any(d => d.Source == themeUri))
                {
                    app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = themeUri });
                }

                if (AppClass.Services is null)
                {
                    var prop = typeof(AppClass).GetProperty("Services", BindingFlags.Public | BindingFlags.Static);
                    prop?.SetValue(null, new AppServices(AppClass.Logger));
                }

                action();
            }
            catch (Exception ex)
            {
                threadEx = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (threadEx is not null)
        {
            throw new AggregateException("STA thread execution failed", threadEx);
        }
    }

    private static void DoEvents()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new DispatcherOperationCallback(f =>
        {
            ((DispatcherFrame)f).Continue = false;
            return null;
        }), frame);
        Dispatcher.PushFrame(frame);
    }

    [Fact]
    public void NavRecentButton_TogglesFlyoutAndRefreshesHistory()
    {
        RunOnSta(() =>
        {
            var window = new MainWindow();
            var flyout = (Border)window.FindName("RecentResultsFlyout");
            var navRecentBtn = (Button)window.FindName("NavRecentBtn");

            Assert.NotNull(flyout);
            Assert.NotNull(navRecentBtn);
            Assert.Equal(Visibility.Collapsed, flyout.Visibility);

            navRecentBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            DoEvents();

            Assert.Equal(Visibility.Visible, flyout.Visibility);

            navRecentBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            DoEvents();

            Assert.Equal(Visibility.Collapsed, flyout.Visibility);
        });
    }

    [Fact]
    public void HistoryStudio_DisplaysRecordedOperationsWithStatus()
    {
        RunOnSta(() =>
        {
            var window = new MainWindow();
            var historyListBox = (ListBox)window.FindName("HistoryListBox");
            var countBadge = (TextBlock)window.FindName("HistoryCountBadge");

            Assert.NotNull(historyListBox);
            Assert.NotNull(countBadge);

            // Clear existing history
            AppClass.Services.HistoryStore.Clear();

            // Record a completed operation
            var tempIn = Path.GetTempFileName();
            var tempOut = Path.GetTempFileName();

            try
            {
                var record = new OperationRecord(
                    Id: "test-rec-1",
                    ActionId: "image.convert",
                    ActionName: "Convert Image",
                    Category: OperationCategory.Image,
                    InputPaths: [tempIn],
                    OutputPaths: [tempOut],
                    StartedAtUtc: DateTimeOffset.UtcNow.AddSeconds(-3),
                    CompletedAtUtc: DateTimeOffset.UtcNow,
                    Duration: TimeSpan.FromSeconds(3),
                    Status: OperationStatus.Completed,
                    Options: new Dictionary<string, string> { ["Format"] = "webp" },
                    PresetName: "WebP High",
                    FailureReason: null,
                    RetryOfOperationId: null);

                AppClass.Services.HistoryStore.RecordOperation(record);
                DoEvents();

                Assert.True(historyListBox.Items.Count > 0);
                var first = historyListBox.Items[0] as HistoryEntryViewModel;
                Assert.NotNull(first);
                Assert.Equal("Convert Image", first.ActionName);
                Assert.Equal(OperationStatus.Completed, first.Status);
                Assert.Equal("✓ Success", first.StatusBadgeText);
                Assert.True(first.IsOutputAvailable);
                Assert.Contains("1 operation", countBadge.Text);
            }
            finally
            {
                if (File.Exists(tempIn)) File.Delete(tempIn);
                if (File.Exists(tempOut)) File.Delete(tempOut);
            }
        });
    }

    [Fact]
    public void HistoryStudio_SearchAndFilterOperations()
    {
        RunOnSta(() =>
        {
            var window = new MainWindow();
            var searchBox = (TextBox)window.FindName("HistorySearchBox");
            var statusFilter = (ComboBox)window.FindName("HistoryStatusFilterCombo");
            var historyListBox = (ListBox)window.FindName("HistoryListBox");

            Assert.NotNull(searchBox);
            Assert.NotNull(statusFilter);
            Assert.NotNull(historyListBox);

            AppClass.Services.HistoryStore.Clear();

            var r1 = new OperationRecord(
                Id: "search-1",
                ActionId: "image.resize",
                ActionName: "Resize Image",
                Category: OperationCategory.Image,
                InputPaths: ["C:\\Photos\\vacation.jpg"],
                OutputPaths: ["C:\\Photos\\vacation_thumb.jpg"],
                StartedAtUtc: DateTimeOffset.UtcNow,
                CompletedAtUtc: DateTimeOffset.UtcNow,
                Duration: TimeSpan.FromMilliseconds(200),
                Status: OperationStatus.Completed,
                Options: new Dictionary<string, string>());

            var r2 = new OperationRecord(
                Id: "search-2",
                ActionId: "pdf.split",
                ActionName: "Split PDF",
                Category: OperationCategory.Pdf,
                InputPaths: ["C:\\Docs\\report.pdf"],
                OutputPaths: [],
                StartedAtUtc: DateTimeOffset.UtcNow,
                CompletedAtUtc: DateTimeOffset.UtcNow,
                Duration: TimeSpan.FromMilliseconds(500),
                Status: OperationStatus.Failed,
                Options: new Dictionary<string, string>(),
                FailureReason: "Disk full");

            AppClass.Services.HistoryStore.RecordOperation(r1);
            AppClass.Services.HistoryStore.RecordOperation(r2);
            DoEvents();

            Assert.Equal(2, historyListBox.Items.Count);

            // Filter by search text
            searchBox.Text = "vacation";
            DoEvents();

            Assert.Single(historyListBox.Items);
            var item = (HistoryEntryViewModel)historyListBox.Items[0];
            Assert.Equal("Resize Image", item.ActionName);

            // Clear search, filter by status: Failed
            searchBox.Text = "";
            statusFilter.SelectedIndex = 2; // Failed
            DoEvents();

            Assert.Single(historyListBox.Items);
            var failedItem = (HistoryEntryViewModel)historyListBox.Items[0];
            Assert.Equal("Split PDF", failedItem.ActionName);
            Assert.Equal(OperationStatus.Failed, failedItem.Status);
        });
    }

    [Fact]
    public void HistoryStudio_InspectOperationDetails_And_RetryButton()
    {
        RunOnSta(() =>
        {
            var window = new MainWindow();
            var historyListBox = (ListBox)window.FindName("HistoryListBox");
            var inspectorCard = (Border)window.FindName("HistoryInspectorCard");
            var inspectorAction = (TextBlock)window.FindName("HistoryInspectorActionName");
            var errorBlock = (Border)window.FindName("HistoryInspectorErrorBlock");
            var retryBtn = (Button)window.FindName("HistoryInspectorRetryBtn");

            Assert.NotNull(historyListBox);
            Assert.NotNull(inspectorCard);
            Assert.NotNull(inspectorAction);
            Assert.NotNull(errorBlock);
            Assert.NotNull(retryBtn);

            AppClass.Services.HistoryStore.Clear();

            var tempInput = Path.GetTempFileName();
            try
            {
                var failedRec = new OperationRecord(
                    Id: "failed-op-1",
                    ActionId: "image.compress",
                    ActionName: "Compress Image",
                    Category: OperationCategory.Image,
                    InputPaths: [tempInput],
                    OutputPaths: [],
                    StartedAtUtc: DateTimeOffset.UtcNow,
                    CompletedAtUtc: DateTimeOffset.UtcNow,
                    Duration: TimeSpan.FromSeconds(1),
                    Status: OperationStatus.Failed,
                    Options: new Dictionary<string, string> { ["Quality"] = "80" },
                    FailureReason: "Simulated encoding error");

                AppClass.Services.HistoryStore.RecordOperation(failedRec);
                DoEvents();

                historyListBox.SelectedIndex = 0;
                DoEvents();

                Assert.Equal(Visibility.Visible, inspectorCard.Visibility);
                Assert.Equal("Compress Image", inspectorAction.Text);
                Assert.Equal(Visibility.Visible, errorBlock.Visibility);
                Assert.Equal(Visibility.Visible, retryBtn.Visibility);
                Assert.True(retryBtn.IsEnabled);
            }
            finally
            {
                if (File.Exists(tempInput)) File.Delete(tempInput);
            }
        });
    }

    [Fact]
    public void HistoryStudio_ClearHistory_RemovesRecords_PreservesDiskFiles()
    {
        RunOnSta(() =>
        {
            var window = new MainWindow();
            var historyListBox = (ListBox)window.FindName("HistoryListBox");

            var tempIn = Path.GetTempFileName();
            var tempOut = Path.GetTempFileName();

            try
            {
                var rec = new OperationRecord(
                    Id: "clear-test-1",
                    ActionId: "image.convert",
                    ActionName: "Convert Image",
                    Category: OperationCategory.Image,
                    InputPaths: [tempIn],
                    OutputPaths: [tempOut],
                    StartedAtUtc: DateTimeOffset.UtcNow,
                    CompletedAtUtc: DateTimeOffset.UtcNow,
                    Duration: TimeSpan.FromMilliseconds(100),
                    Status: OperationStatus.Completed,
                    Options: new Dictionary<string, string>());

                AppClass.Services.HistoryStore.RecordOperation(rec);
                DoEvents();

                Assert.NotEmpty(historyListBox.Items);

                // Clear history
                AppClass.Services.HistoryStore.Clear();
                DoEvents();

                Assert.Empty(historyListBox.Items);

                // Critical safety requirement: input and output files on disk MUST remain untouched
                Assert.True(File.Exists(tempIn), "Input file must not be deleted on history clear");
                Assert.True(File.Exists(tempOut), "Output file must not be deleted on history clear");
            }
            finally
            {
                if (File.Exists(tempIn)) File.Delete(tempIn);
                if (File.Exists(tempOut)) File.Delete(tempOut);
            }
        });
    }

    [Fact]
    public void HistoryStudio_SavedWorkflowsTab_ListsWorkflows_AndInspects()
    {
        RunOnSta(() =>
        {
            var window = new MainWindow();
            var historyTabBtn = (Button)window.FindName("HistoryTabBtn");
            var workflowsTabBtn = (Button)window.FindName("WorkflowsTabBtn");
            var historyPanel = (Grid)window.FindName("HistoryViewPanel");
            var workflowsPanel = (Grid)window.FindName("WorkflowsViewPanel");
            var workflowsListBox = (ListBox)window.FindName("WorkflowsListBox");
            var inspectorCard = (Border)window.FindName("WorkflowInspectorCard");
            var inspectorName = (TextBlock)window.FindName("WorkflowInspectorName");

            Assert.NotNull(historyTabBtn);
            Assert.NotNull(workflowsTabBtn);
            Assert.NotNull(historyPanel);
            Assert.NotNull(workflowsPanel);
            Assert.NotNull(workflowsListBox);
            Assert.NotNull(inspectorCard);
            Assert.NotNull(inspectorName);

            // Switch to Saved Workflows tab
            workflowsTabBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            DoEvents();

            Assert.Equal(Visibility.Collapsed, historyPanel.Visibility);
            Assert.Equal(Visibility.Visible, workflowsPanel.Visibility);

            // Built-in workflows should be loaded
            Assert.NotEmpty(workflowsListBox.Items);

            workflowsListBox.SelectedIndex = 0;
            DoEvents();

            Assert.Equal(Visibility.Visible, inspectorCard.Visibility);
            Assert.False(string.IsNullOrWhiteSpace(inspectorName.Text));

            // Switch back to History tab
            historyTabBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            DoEvents();

            Assert.Equal(Visibility.Visible, historyPanel.Visibility);
            Assert.Equal(Visibility.Collapsed, workflowsPanel.Visibility);
        });
    }
}
