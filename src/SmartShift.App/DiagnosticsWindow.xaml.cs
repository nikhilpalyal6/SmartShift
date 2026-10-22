using System.Text;
using System.Windows;
using SmartShift.Core.Logging;
using SmartShift.Windows.Input;
using SmartShift.Windows.Ui;

namespace SmartShift.App;

public partial class DiagnosticsWindow : Window
{
    private DropShelfWindow? _shelf;
    private CursorProbeWindow? _probe;
    private ObserveOnlyMouseHook? _hook;

    public DiagnosticsWindow()
    {
        InitializeComponent();
        App.Logger.EntryAdded += OnLogEntry;
        Closed += (_, _) => Cleanup();
        AppendExistingLog();
    }

    private void ShowShelf_Click(object sender, RoutedEventArgs e)
    {
        if (_shelf is { IsVisible: true })
        {
            _shelf.Activate();
            UpdateStatus();
            return;
        }

        _shelf = new DropShelfWindow(App.Logger);
        _shelf.Closed += (_, _) =>
        {
            _shelf = null;
            UpdateStatus();
        };
        _shelf.Show();
        UpdateStatus();
    }

    private void HideShelf_Click(object sender, RoutedEventArgs e)
    {
        _shelf?.Close();
        _shelf = null;
        UpdateStatus();
    }

    private void HookToggle_Checked(object sender, RoutedEventArgs e)
    {
        try
        {
            _hook = new ObserveOnlyMouseHook(App.Logger);
            _hook.Start();
        }
        catch (Exception ex)
        {
            App.Logger.Error("ui", "Could not start observe-only hook.", ex);
            HookToggle.IsChecked = false;
        }

        UpdateStatus();
    }

    private void HookToggle_Unchecked(object sender, RoutedEventArgs e)
    {
        _hook?.Dispose();
        _hook = null;
        UpdateStatus();
    }

    private void ProbeToggle_Checked(object sender, RoutedEventArgs e)
    {
        _probe = new CursorProbeWindow(App.Logger);
        _probe.Closed += (_, _) =>
        {
            _probe = null;
            ProbeToggle.IsChecked = false;
            UpdateStatus();
        };
        _probe.Show();
        App.Logger.Warn("ui", "Cursor drop probe is visible and can intercept drops under the pointer.");
        UpdateStatus();
    }

    private void ProbeToggle_Unchecked(object sender, RoutedEventArgs e)
    {
        _probe?.Close();
        _probe = null;
        UpdateStatus();
    }

    private void Cleanup()
    {
        App.Logger.EntryAdded -= OnLogEntry;
        _probe?.Close();
        _shelf?.Close();
        _hook?.Dispose();
        _probe = null;
        _shelf = null;
        _hook = null;
    }

    private void UpdateStatus()
    {
        StatusText.Text =
            $"Phase 0 shelf {(_shelf is { IsVisible: true } ? "visible" : "hidden")}. " +
            $"Hook {(_hook?.IsInstalled == true ? "on (observe only)" : "off")}. " +
            $"Probe {(_probe is { IsVisible: true } ? "on (can intercept)" : "off")}.";
    }

    private void OnLogEntry(DiagnosticEntry entry)
    {
        Dispatcher.Invoke(() =>
        {
            LogBox.AppendText(entry + Environment.NewLine);
            LogBox.ScrollToEnd();
        });
    }

    private void AppendExistingLog()
    {
        var builder = new StringBuilder();
        foreach (var entry in App.Logger.Snapshot())
        {
            builder.AppendLine(entry.ToString());
        }

        LogBox.Text = builder.ToString();
    }
}
