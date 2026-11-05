using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using SmartShift.App.Ui;
using SmartShift.Core.Actions;
using SmartShift.Core.Classification;
using SmartShift.Core.Images;
using SmartShift.Processing.Batch;
using SmartShift.Processing.Images;

namespace SmartShift.App;

public partial class MainWindow
{
    private bool _isInitializingPresets;

    private void InitializeBatchPresets()
    {
        if (BatchPresetCombo is null) return;
        _isInitializingPresets = true;

        try
        {
            var presets = App.Services.ImagePresets.GetAll();
            BatchPresetCombo.Items.Clear();

            var customItem = new ComboBoxItem { Content = "-- Custom Settings --", Tag = null };
            BatchPresetCombo.Items.Add(customItem);

            foreach (var preset in presets)
            {
                var item = new ComboBoxItem
                {
                    Content = $"{preset.Name}{(preset.IsBuiltIn ? " [Built-In]" : "")}",
                    Tag = preset
                };
                BatchPresetCombo.Items.Add(item);
            }

            // Default to Custom Settings if nothing selected yet
            if (BatchPresetCombo.SelectedIndex < 0)
            {
                BatchPresetCombo.SelectedIndex = 0;
            }
        }
        finally
        {
            _isInitializingPresets = false;
        }
    }

    private void BatchPresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializingPresets) return;
        if (BatchPresetCombo.SelectedItem is not ComboBoxItem { Tag: ImageOptimizationPreset preset }) return;

        // Apply preset format
        var format = preset.Format.ToLowerInvariant();
        if (format is "webp" or "jpeg" or "jpg" or "png")
        {
            if (format == "webp" && preset.Quality == 80 && !preset.TargetSizeKb.HasValue && !preset.ResizeWidth.HasValue)
            {
                _selectedBatchActionId = ActionIds.CompressImage;
            }
            else
            {
                _selectedBatchActionId = "image.convert";
                _selectedBatchTargetFormat = format is "jpg" ? "jpeg" : format;
            }
        }

        // Apply quality
        if (BatchCompressQualitySlider is not null)
        {
            BatchCompressQualitySlider.Value = preset.Quality;
        }
        if (BatchConvertQualitySlider is not null)
        {
            BatchConvertQualitySlider.Value = preset.Quality;
        }

        // Apply target size
        if (BatchEnableTargetSizeCheck is not null)
        {
            if (preset.TargetSizeKb.HasValue && preset.TargetSizeKb.Value > 0)
            {
                BatchEnableTargetSizeCheck.IsChecked = true;
                if (BatchTargetSizeBox is not null)
                {
                    BatchTargetSizeBox.Text = preset.TargetSizeKb.Value.ToString(CultureInfo.InvariantCulture);
                }
                if (BatchTargetSizeUnitCombo is not null)
                {
                    BatchTargetSizeUnitCombo.SelectedIndex = 0; // KB
                }
            }
            else
            {
                BatchEnableTargetSizeCheck.IsChecked = false;
            }
        }

        // Apply metadata strip
        if (BatchStripMetadataCheck is not null)
        {
            BatchStripMetadataCheck.IsChecked = preset.StripMetadata;
        }

        // Apply resize if preset has dimensions
        if (preset.ResizeWidth.HasValue)
        {
            _selectedBatchActionId = ActionIds.ResizeImage;
            if (BatchResizeWidthBox is not null) BatchResizeWidthBox.Text = preset.ResizeWidth.Value.ToString();
            if (BatchResizeHeightBox is not null && preset.ResizeHeight.HasValue) BatchResizeHeightBox.Text = preset.ResizeHeight.Value.ToString();
            if (BatchResizeKeepAspectCheck is not null) BatchResizeKeepAspectCheck.IsChecked = preset.KeepAspect;
        }

        // Apply naming template if preset has one
        if (!string.IsNullOrWhiteSpace(preset.NamingTemplate) && BatchNamingTemplateBox is not null)
        {
            BatchNamingTemplateBox.Text = preset.NamingTemplate;
        }

        // Update UI buttons
        SyncBatchOperationButtons();
        UpdateBatchNamingPreview();
    }

    private void SyncBatchOperationButtons()
    {
        if (BatchOpCompressBtn is null) return;

        BatchOpCompressBtn.Style = (Style)FindResource(_selectedBatchActionId == ActionIds.CompressImage ? "PrimaryButton" : "SecondaryButton");
        BatchOpConvertBtn.Style = (Style)FindResource(_selectedBatchActionId == "image.convert" ? "PrimaryButton" : "SecondaryButton");
        BatchOpResizeBtn.Style = (Style)FindResource(_selectedBatchActionId == ActionIds.ResizeImage ? "PrimaryButton" : "SecondaryButton");
        BatchOpMetadataBtn.Style = (Style)FindResource(_selectedBatchActionId == ActionIds.RemoveImageMetadata ? "PrimaryButton" : "SecondaryButton");

        BatchCompressSettingsPanel.Visibility = _selectedBatchActionId == ActionIds.CompressImage ? Visibility.Visible : Visibility.Collapsed;
        BatchConvertSettingsPanel.Visibility = _selectedBatchActionId == "image.convert" ? Visibility.Visible : Visibility.Collapsed;
        BatchResizeSettingsPanel.Visibility = _selectedBatchActionId == ActionIds.ResizeImage ? Visibility.Visible : Visibility.Collapsed;
        BatchMetadataSettingsPanel.Visibility = _selectedBatchActionId == ActionIds.RemoveImageMetadata ? Visibility.Visible : Visibility.Collapsed;

        if (_selectedBatchActionId == "image.convert")
        {
            BatchTargetWebpBtn.Style = (Style)FindResource(_selectedBatchTargetFormat == "webp" ? "PrimaryButton" : "SecondaryButton");
            BatchTargetPngBtn.Style = (Style)FindResource(_selectedBatchTargetFormat == "png" ? "PrimaryButton" : "SecondaryButton");
            BatchTargetJpegBtn.Style = (Style)FindResource(_selectedBatchTargetFormat == "jpeg" ? "PrimaryButton" : "SecondaryButton");
            BatchConvertQualityPanel.Visibility = _selectedBatchTargetFormat == "png" ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private void BatchSavePreset_Click(object sender, RoutedEventArgs e)
    {
        string presetFormat = _selectedBatchActionId switch
        {
            "image.convert" => _selectedBatchTargetFormat,
            ActionIds.CompressImage => "webp",
            _ => "webp"
        };

        int quality = (int)(BatchConvertQualitySlider?.Value ?? 80);
        long? targetKb = null;
        if (BatchEnableTargetSizeCheck?.IsChecked == true && long.TryParse(BatchTargetSizeBox?.Text, out var val))
        {
            if (BatchTargetSizeUnitCombo?.SelectedIndex == 1) val *= 1024;
            targetKb = val;
        }

        var customPreset = new ImageOptimizationPreset(
            Id: $"custom-preset-{DateTime.Now.Ticks}",
            Name: $"My Preset ({presetFormat.ToUpperInvariant()} {quality}%)",
            Description: "User-saved custom image workflow preset",
            Format: presetFormat,
            Quality: quality,
            TargetSizeKb: targetKb,
            StripMetadata: BatchStripMetadataCheck?.IsChecked == true,
            NamingTemplate: BatchNamingTemplateBox?.Text);

        try
        {
            App.Services.ImagePresets.SavePreset(customPreset);
            InitializeBatchPresets();
            ShowBanner($"Preset '{customPreset.Name}' saved successfully.", error: false);
        }
        catch (Exception ex)
        {
            ShowBanner($"Failed to save preset: {ex.Message}", error: true);
        }
    }

    private void BatchEnableTargetSizeCheck_Toggled(object sender, RoutedEventArgs e)
    {
        if (BatchTargetSizeInputPanel is not null)
        {
            BatchTargetSizeInputPanel.Visibility = BatchEnableTargetSizeCheck.IsChecked == true
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    private void BatchNamingTemplateBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateBatchNamingPreview();
    }

    private void BatchNamingToken_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string token } && BatchNamingTemplateBox is not null)
        {
            var cur = BatchNamingTemplateBox.Text;
            var caret = BatchNamingTemplateBox.CaretIndex;
            if (caret >= 0 && caret <= cur.Length)
            {
                BatchNamingTemplateBox.Text = cur.Insert(caret, token);
                BatchNamingTemplateBox.CaretIndex = caret + token.Length;
            }
            else
            {
                BatchNamingTemplateBox.Text += token;
            }
        }
    }

    private void UpdateBatchNamingPreview()
    {
        if (BatchNamingPreviewText is null || BatchNamingTemplateBox is null) return;

        var sampleFile = _batchFiles.Count > 0 ? _batchFiles[0].FileName : "photo.jpg";
        var sampleExt = _selectedBatchActionId switch
        {
            "image.convert" => "." + _selectedBatchTargetFormat,
            ActionIds.ConvertToWebp => ".webp",
            ActionIds.ConvertToPng => ".png",
            ActionIds.ConvertToJpeg => ".jpg",
            _ => Path.GetExtension(sampleFile)
        };

        if (string.IsNullOrWhiteSpace(sampleExt)) sampleExt = ".webp";

        var preview = ImageNamingTemplate.GeneratePreview(
            BatchNamingTemplateBox.Text,
            sampleFile,
            sampleExt,
            sampleIndex: 0);

        BatchNamingPreviewText.Text = $"Preview: {preview}";
    }

    private void UpdateBatchInspectorMetadata(BatchQueueItemViewModel item)
    {
        if (item is null) return;

        try
        {
            if (item.Metadata is null && File.Exists(item.File.Path))
            {
                item.Metadata = ImageMetadataReader.Read(item.File.Path);
            }

            if (item.Metadata is not null)
            {
                BatchInspectorMetadataCard.Visibility = Visibility.Visible;
                BatchInspectorMetaDims.Text = $"Dimensions: {item.Metadata.DimensionsText} ({item.Metadata.AspectRatioText})";
                BatchInspectorMetaFormat.Text = $"Format: {item.Metadata.Format} | Color: {item.Metadata.ColorType} | Alpha: {item.Metadata.AlphaType}";
                BatchInspectorMetaExif.Text = $"Orientation: {item.Metadata.ExifOrientation} ({item.Metadata.RotationDegrees}°)";
                BatchInspectorMetaTags.Text = $"Signatures: {item.Metadata.MetadataSummary}";
            }
            else
            {
                BatchInspectorMetadataCard.Visibility = Visibility.Collapsed;
            }
        }
        catch
        {
            BatchInspectorMetadataCard.Visibility = Visibility.Collapsed;
        }
    }

    private async void BatchRetryFailed_Click(object sender, RoutedEventArgs e)
    {
        if (_isBatchRunning) return;

        var failedItems = _batchFiles.Where(b => b.Status == BatchItemStatus.Failed).ToList();
        if (failedItems.Count == 0)
        {
            ShowBanner("No failed items to retry.", error: false, warning: true);
            return;
        }

        foreach (var item in failedItems)
        {
            item.Status = BatchItemStatus.Pending;
            item.StatusText = "Pending Retry";
            item.ResultMessage = null;
        }

        BatchRetryFailedButton.Visibility = Visibility.Collapsed;
        StartBatchProcess_Click(sender, e);
    }
}
