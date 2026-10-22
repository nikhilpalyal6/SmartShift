using System.Windows;
using SmartShift.Core.Drops;
using SmartShift.Core.Logging;
using SmartShift.Windows.DragDrop;

namespace SmartShift.Windows.Ui;

public partial class DropShelfWindow : Window
{
    private readonly DiagnosticLogger _logger;

    public DropShelfWindow(DiagnosticLogger logger)
    {
        _logger = logger;
        InitializeComponent();
        Left = SystemParameters.WorkArea.Right - Width - 24;
        Top = SystemParameters.WorkArea.Bottom - Height - 24;
        Closed += (_, _) => _logger.Info("dropshelf", "Drop Shelf closed.");
    }

    public FileDropInspection? LastInspection { get; private set; }

    public event Action<DropExtractionResult>? DropInspected;

    protected override void OnDragEnter(DragEventArgs e)
    {
        HandleDrag(e, preview: true);
        base.OnDragEnter(e);
    }

    protected override void OnDragOver(DragEventArgs e)
    {
        HandleDrag(e, preview: true);
        base.OnDragOver(e);
    }

    protected override void OnDrop(DragEventArgs e)
    {
        HandleDrag(e, preview: false);
        base.OnDrop(e);
    }

    private void HandleDrag(DragEventArgs e, bool preview)
    {
        try
        {
            var result = OleFileDropExtractor.Extract(e.Data);
            e.Effects = OleFileDropExtractor.SafeAllowedEffects(result.HasFileDrop);
            e.Handled = true;

            if (preview)
            {
                _logger.Info("dropshelf", "Drag over Drop Shelf.", new Dictionary<string, string>
                {
                    ["modifiers"] = result.Modifiers.Describe(),
                    ["hasFiles"] = result.HasFileDrop.ToString(),
                    ["formats"] = string.Join(",", result.Inspection.AvailableFormats)
                });
                return;
            }

            LastInspection = result.Inspection;
            ApplyInspection(result.Inspection);
            DropInspected?.Invoke(result);
            _logger.Info("dropshelf", result.Inspection.Status, new Dictionary<string, string>
            {
                ["fileCount"] = result.Inspection.FileCount.ToString(),
                ["supported"] = result.Inspection.SupportedCount.ToString(),
                ["modifiers"] = result.Modifiers.Describe()
            });

            if (!FileDropInspector.OriginalFilesUnchanged(result.Inspection.Files, out var details))
            {
                _logger.Error("dropshelf", details);
            }
        }
        catch (Exception ex)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            _logger.Error("dropshelf", "Drop handling failed.", ex);
            SummaryText.Text = "The drop could not be inspected. Original files were not modified.";
        }
    }

    private void ApplyInspection(FileDropInspection inspection)
    {
        SummaryText.Text = $"{inspection.Status} Files: {inspection.FileCount}. Supported: {inspection.SupportedCount}.";
        FileList.Items.Clear();
        foreach (var file in inspection.Files)
        {
            var tag = file.Kind == SupportedFileKind.Unsupported ? "unsupported" : file.Kind.ToString().ToLowerInvariant();
            FileList.Items.Add($"{file.FileName} [{tag}]");
        }

        foreach (var reason in inspection.RejectedReasons)
        {
            FileList.Items.Add($"Rejected: {reason}");
        }
    }
}
