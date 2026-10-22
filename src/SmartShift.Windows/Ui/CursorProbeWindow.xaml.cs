using System.Windows;
using System.Windows.Threading;
using SmartShift.Core.Logging;
using SmartShift.Windows.DragDrop;

namespace SmartShift.Windows.Ui;

/// <summary>
/// Optional near-cursor drop target used only for gesture experiments.
/// When shown it can intercept drops under the cursor — keep it off unless testing.
/// </summary>
public partial class CursorProbeWindow : Window
{
    private readonly DiagnosticLogger _logger;
    private readonly DispatcherTimer _timer;

    public CursorProbeWindow(DiagnosticLogger logger)
    {
        _logger = logger;
        InitializeComponent();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        _timer.Tick += (_, _) => FollowCursor();
        Loaded += (_, _) => _timer.Start();
        Closed += (_, _) => _timer.Stop();
    }

    protected override void OnDragOver(DragEventArgs e)
    {
        var result = OleFileDropExtractor.Extract(e.Data);
        e.Effects = OleFileDropExtractor.SafeAllowedEffects(result.HasFileDrop);
        e.Handled = true;
        ProbeText.Text = result.HasFileDrop
            ? $"{result.Inspection.FileCount} file(s) · {result.Modifiers.Describe()}"
            : $"No FileDrop · {result.Modifiers.Describe()}";
        base.OnDragOver(e);
    }

    protected override void OnDrop(DragEventArgs e)
    {
        var result = OleFileDropExtractor.Extract(e.Data);
        e.Effects = OleFileDropExtractor.SafeAllowedEffects(result.HasFileDrop);
        e.Handled = true;
        ProbeText.Text = result.Inspection.Status;
        _logger.Info("probe", "Cursor probe received a drop.", new Dictionary<string, string>
        {
            ["status"] = result.Inspection.Status,
            ["modifiers"] = result.Modifiers.Describe()
        });
        base.OnDrop(e);
    }

    private void FollowCursor()
    {
        if (NativeCursor.TryGetCursorPos(out var x, out var y))
        {
            Left = x + 18;
            Top = y + 18;
        }
    }
}

internal static class NativeCursor
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point pt);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    public static bool TryGetCursorPos(out int x, out int y)
    {
        if (GetCursorPos(out var pt))
        {
            x = pt.X;
            y = pt.Y;
            return true;
        }

        x = 0;
        y = 0;
        return false;
    }
}
