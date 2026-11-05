using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SmartShift.Core.Actions;
using SmartShift.Core.Classification;
using SmartShift.Core.IO;
using SmartShift.Processing;
using SmartShift.Processing.Conversion;

namespace SmartShift.App;

public partial class MainWindow
{
    private bool _isUnifiedConvertActive;
    private DetectedFormat? _selectedUnifiedTargetFormat;
    private string _unifiedPdfPptxMode = "pageAsImage";
    private string _unifiedPdfDpi = "150";

    public bool IsUnifiedConvertActive => _isUnifiedConvertActive;
    public DetectedFormat? SelectedUnifiedTargetFormat => _selectedUnifiedTargetFormat;

    private void HeroConvert_Click(object sender, RoutedEventArgs e)
    {
        OpenUnifiedConvert();
    }

    public void OpenUnifiedConvert(DetectedFormat? preselectedTarget = null)
    {
        if (_selectedFileItem is null) return;
        var file = _selectedFileItem.File;

        // Deselect radial menu items
        foreach (var r in _radialItems)
        {
            r.IsSelected = false;
        }
        _selectedAction = null;
        _isUnifiedConvertActive = true;

        ClosePdfPreview();
        CloseImageEditor();

        NoActionPlaceholder.Visibility = Visibility.Collapsed;
        ActionConfigPanel.Visibility = Visibility.Visible;
        ActionTitleText.Text = "Universal Convert";
        ActionGoalText.Text = "Convert to supported format";
        ActionDescText.Text = "Select a target format and configure conversion parameters for this file.";

        HideAllActionSubviews();
        UnifiedConvertOptionsView.Visibility = Visibility.Visible;

        // Populate Source Info
        UnifiedSourceFileNameText.Text = file.FileName;
        UnifiedSourceFormatBadge.Text = file.Format.ToString().ToUpperInvariant();
        UnifiedSourceFileSizeText.Text = _selectedFileItem.SizeText;

        // Query supported targets dynamically from UnifiedConversion service
        var targets = App.Services.UnifiedConversion.GetSupportedTargets(file.Format);

        UnifiedTargetFormatsPanel.Children.Clear();

        if (targets.Count == 0)
        {
            ActionAvailabilityBadge.Background = (Brush)FindResource("DangerSoftBrush");
            ActionAvailabilityText.Text = "Unavailable";
            ActionAvailabilityText.Foreground = (Brush)FindResource("DangerBrush");

            UnifiedNoTargetsCard.Visibility = Visibility.Visible;
            UnifiedNoTargetsMessage.Text = $"SmartShift does not support converting {file.DisplayType} files in this version.";
            UnifiedDynamicSettingsHost.Visibility = Visibility.Collapsed;
            RunButton.IsEnabled = false;
            return;
        }

        ActionAvailabilityBadge.Background = (Brush)FindResource("OkSoftBrush");
        ActionAvailabilityText.Text = "Available";
        ActionAvailabilityText.Foreground = (Brush)FindResource("OkBrush");

        UnifiedNoTargetsCard.Visibility = Visibility.Collapsed;
        UnifiedDynamicSettingsHost.Visibility = Visibility.Visible;
        RunButton.IsEnabled = true;

        // Choose initial target format
        var initialTarget = preselectedTarget.HasValue && targets.Contains(preselectedTarget.Value)
            ? preselectedTarget.Value
            : targets[0];

        foreach (var target in targets)
        {
            var btn = new Button
            {
                Content = GetTargetButtonLabel(target),
                Tag = target,
                Margin = new Thickness(0, 0, 8, 6),
                Padding = new Thickness(12, 6, 12, 6),
                Style = (Style)FindResource(target == initialTarget ? "PrimaryButton" : "SecondaryButton")
            };
            btn.Click += UnifiedTargetFormatButton_Click;
            UnifiedTargetFormatsPanel.Children.Add(btn);
        }

        SelectUnifiedTarget(initialTarget);
    }

    private void HideAllActionSubviews()
    {
        if (CompressOptionsView is not null) CompressOptionsView.Visibility = Visibility.Collapsed;
        if (WebpOptionsView is not null) WebpOptionsView.Visibility = Visibility.Collapsed;
        if (ResizeOptionsView is not null) ResizeOptionsView.Visibility = Visibility.Collapsed;
        if (MetadataOptionsView is not null) MetadataOptionsView.Visibility = Visibility.Collapsed;
        if (InfoOptionsView is not null) InfoOptionsView.Visibility = Visibility.Collapsed;
        if (PdfCompressOptionsView is not null) PdfCompressOptionsView.Visibility = Visibility.Collapsed;
        if (PdfExportOptionsView is not null) PdfExportOptionsView.Visibility = Visibility.Collapsed;
        if (PdfTextOptionsView is not null) PdfTextOptionsView.Visibility = Visibility.Collapsed;
        if (PdfDocxOptionsView is not null) PdfDocxOptionsView.Visibility = Visibility.Collapsed;
        if (PdfPptxOptionsView is not null) PdfPptxOptionsView.Visibility = Visibility.Collapsed;
        if (PdfPreviewOptionsView is not null) PdfPreviewOptionsView.Visibility = Visibility.Collapsed;
        if (EditImageOptionsView is not null) EditImageOptionsView.Visibility = Visibility.Collapsed;
        if (UnifiedConvertOptionsView is not null) UnifiedConvertOptionsView.Visibility = Visibility.Collapsed;
    }

    private static string GetTargetButtonLabel(DetectedFormat format) => format switch
    {
        DetectedFormat.Webp => "WebP",
        DetectedFormat.Png => "PNG",
        DetectedFormat.Jpeg => "JPEG",
        DetectedFormat.Docx => "Word (.docx)",
        DetectedFormat.Pptx => "PowerPoint (.pptx)",
        DetectedFormat.Txt => "Plain Text (.txt)",
        _ => format.ToString().ToUpperInvariant()
    };

    private void UnifiedTargetFormatButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is DetectedFormat target)
        {
            SelectUnifiedTarget(target);
        }
    }

    public void SelectUnifiedTarget(DetectedFormat target)
    {
        _selectedUnifiedTargetFormat = target;

        // Update button styles in UnifiedTargetFormatsPanel
        foreach (var child in UnifiedTargetFormatsPanel.Children)
        {
            if (child is Button b && b.Tag is DetectedFormat fmt)
            {
                b.Style = (Style)FindResource(fmt == target ? "PrimaryButton" : "SecondaryButton");
            }
        }

        var sourceFormat = _selectedFileItem?.File.Format ?? DetectedFormat.Unknown;

        // Reset settings visibility
        UnifiedImageQualityPanel.Visibility = Visibility.Collapsed;
        UnifiedPdfRangePanel.Visibility = Visibility.Collapsed;
        UnifiedPptxModePanel.Visibility = Visibility.Collapsed;
        UnifiedPdfDpiPanel.Visibility = Visibility.Collapsed;

        // Configure dynamic settings based on source & target
        if (sourceFormat is DetectedFormat.Png or DetectedFormat.Jpeg or DetectedFormat.Webp)
        {
            if (target is DetectedFormat.Webp or DetectedFormat.Jpeg)
            {
                UnifiedImageQualityPanel.Visibility = Visibility.Visible;
                UnifiedQualityHintText.Text = target == DetectedFormat.Webp
                    ? "Converts to modern WebP. Quality 100 activates lossless WebP mode. Alpha transparency is preserved."
                    : "Converts to standard lossy JPEG. Alpha transparency will be composited onto a clean background.";
            }

            UnifiedFormatGuaranteeNote.Text = target == DetectedFormat.Png
                ? "✓ Lossless PNG encoding. Original alpha channel and dimensions are strictly preserved."
                : "✓ High efficiency compression. Original source file is strictly untouched.";
        }
        else if (sourceFormat == DetectedFormat.Pdf)
        {
            UnifiedPdfRangePanel.Visibility = Visibility.Visible;

            if (target == DetectedFormat.Docx)
            {
                UnifiedFormatGuaranteeNote.Text = "✓ Reconstructs paragraphs, text lines, font metrics, and embedded figures into a standard Office Open XML (.docx) document.";
            }
            else if (target == DetectedFormat.Pptx)
            {
                UnifiedPptxModePanel.Visibility = Visibility.Visible;
                UnifiedFormatGuaranteeNote.Text = "✓ Generates Microsoft PowerPoint (.pptx) presentation. Non-destructive: source PDF is strictly unmodified.";
            }
            else if (target == DetectedFormat.Txt)
            {
                UnifiedFormatGuaranteeNote.Text = "✓ Extracted in natural document reading order with clean page separators and UTF-8 encoding.";
            }
            else if (target is DetectedFormat.Png or DetectedFormat.Jpeg)
            {
                UnifiedPdfDpiPanel.Visibility = Visibility.Visible;
                if (target == DetectedFormat.Jpeg)
                {
                    UnifiedImageQualityPanel.Visibility = Visibility.Visible;
                    UnifiedQualityHintText.Text = "JPEG quality for exported page images (1–100).";
                }
                UnifiedFormatGuaranteeNote.Text = "✓ Exports PDF pages as standalone high-resolution raster images.";
            }
        }

        ValidateUnifiedConvertInput();
    }

    private void ValidateUnifiedConvertInput()
    {
        if (!_isUnifiedConvertActive) return;

        if (_selectedUnifiedTargetFormat is null)
        {
            RunButton.IsEnabled = false;
            return;
        }

        var sourceFormat = _selectedFileItem?.File.Format ?? DetectedFormat.Unknown;
        if (sourceFormat == DetectedFormat.Pdf)
        {
            var range = UnifiedPdfRangeBox.Text.Trim();
            if (!string.IsNullOrWhiteSpace(range) && !PageRangeParser.TryValidateSyntax(range, out var err))
            {
                ValidationText.Text = err;
                ValidationText.Visibility = Visibility.Visible;
                RunButton.IsEnabled = false;
                return;
            }
        }

        if (UnifiedImageQualityPanel.Visibility == Visibility.Visible)
        {
            if (!int.TryParse(UnifiedQualityBox.Text, out int q) || q < 1 || q > 100)
            {
                ValidationText.Text = "Quality must be between 1 and 100.";
                ValidationText.Visibility = Visibility.Visible;
                RunButton.IsEnabled = false;
                return;
            }
        }

        ValidationText.Visibility = Visibility.Collapsed;
        RunButton.IsEnabled = true;
    }

    private void UnifiedQualitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (UnifiedQualityBox is not null)
        {
            int val = (int)Math.Round(e.NewValue);
            UnifiedQualityBox.Text = val.ToString();
            if (UnifiedQualityValueBadge is not null)
            {
                UnifiedQualityValueBadge.Text = $"{val}%";
            }
            ValidateUnifiedConvertInput();
        }
    }

    private void UnifiedQualityBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (int.TryParse(UnifiedQualityBox.Text, out int val) && val >= 1 && val <= 100)
        {
            if (UnifiedQualitySlider is not null && (int)UnifiedQualitySlider.Value != val)
            {
                UnifiedQualitySlider.Value = val;
            }
            if (UnifiedQualityValueBadge is not null)
            {
                UnifiedQualityValueBadge.Text = $"{val}%";
            }
        }
        ValidateUnifiedConvertInput();
    }

    private void UnifiedPdfRangeBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ValidateUnifiedConvertInput();
    }

    private void UnifiedPptxMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string mode)
        {
            _unifiedPdfPptxMode = mode;
            UnifiedPptxModeImageBtn.Style = (Style)FindResource(mode == "pageAsImage" ? "PrimaryButton" : "SecondaryButton");
            UnifiedPptxModeEditableBtn.Style = (Style)FindResource(mode == "editable" ? "PrimaryButton" : "SecondaryButton");
            UnifiedPptxModeHint.Text = mode == "pageAsImage"
                ? "Page-as-Image: Best visual fidelity. Renders each page as a pixel-accurate slide."
                : "Editable: Reconstructs text boxes and shape objects as editable elements.";
        }
    }

    private void UnifiedDpi_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string dpi)
        {
            _unifiedPdfDpi = dpi;
            UnifiedDpi72Btn.Style = (Style)FindResource(dpi == "72" ? "PrimaryButton" : "SecondaryButton");
            UnifiedDpi150Btn.Style = (Style)FindResource(dpi == "150" ? "PrimaryButton" : "SecondaryButton");
            UnifiedDpi300Btn.Style = (Style)FindResource(dpi == "300" ? "PrimaryButton" : "SecondaryButton");
        }
    }

    private async Task RunUnifiedConvertAsync()
    {
        if (_selectedFileItem is null || _selectedUnifiedTargetFormat is null)
        {
            return;
        }

        ValidateUnifiedConvertInput();
        if (!RunButton.IsEnabled) return;

        var optionsDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (UnifiedImageQualityPanel.Visibility == Visibility.Visible)
        {
            optionsDict["quality"] = UnifiedQualityBox.Text.Trim();
        }
        if (UnifiedPdfRangePanel.Visibility == Visibility.Visible)
        {
            optionsDict["pageRange"] = string.IsNullOrWhiteSpace(UnifiedPdfRangeBox.Text) ? "all" : UnifiedPdfRangeBox.Text.Trim();
        }
        if (UnifiedPptxModePanel.Visibility == Visibility.Visible)
        {
            optionsDict["slideMode"] = _unifiedPdfPptxMode;
        }
        if (UnifiedPdfDpiPanel.Visibility == Visibility.Visible)
        {
            optionsDict["dpi"] = _unifiedPdfDpi;
        }

        _runCts?.Cancel();
        _runCts = new CancellationTokenSource();
        var token = _runCts.Token;

        _busy = true;
        RunButton.IsEnabled = false;
        ProgressHost.Visibility = Visibility.Visible;
        ProgressBarCtrl.IsIndeterminate = true;
        ProgressText.Text = "Converting…";
        ResultCardHost.Visibility = Visibility.Collapsed;

        try
        {
            var progress = new Progress<ProcessingProgress>(p =>
            {
                Dispatcher.InvokeAsync(() =>
                {
                    ProgressBarCtrl.IsIndeterminate = false;
                    ProgressBarCtrl.Value = p.Percent;
                    ProgressText.Text = $"{p.Percent}% — {p.Message}";
                });
            });

            var result = await App.Services.UnifiedConversion.ConvertAsync(
                _selectedFileItem.File,
                _selectedUnifiedTargetFormat.Value,
                optionsDict,
                App.Services.Outputs.RootDirectory,
                progress,
                token).ConfigureAwait(true);

            _lastOutputPath = result.OutputPath;

            var summary = ProcessingResultSummary.FromResult(
                result.Succeeded,
                result.WasCancelled,
                result.OriginalUnchanged,
                result.Status,
                result.OutputPath,
                result.Details,
                _selectedFileItem.File);

            DisplayResult(summary);

            RevealResultButton.Visibility = File.Exists(_lastOutputPath ?? "") ? Visibility.Visible : Visibility.Collapsed;

            if (result.Succeeded)
            {
                ShowBanner(summary.Status, error: false, warning: false);
            }
            else if (!result.WasCancelled)
            {
                ShowBanner(summary.Status, error: true, warning: false);
            }
        }
        catch (OperationCanceledException)
        {
            ProgressText.Text = "Conversion cancelled.";
            ShowBanner("Conversion cancelled.", error: false, warning: true);
        }
        catch (Exception ex)
        {
            ShowBanner($"Conversion failed: {ex.Message}", error: true, warning: false);
        }
        finally
        {
            _busy = false;
            ProgressHost.Visibility = Visibility.Collapsed;
            RunButton.IsEnabled = true;
        }
    }
}
