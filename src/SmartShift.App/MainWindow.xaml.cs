using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;
using SmartShift.App.Ui;
using SmartShift.Core.Actions;
using SmartShift.Core.Classification;
using SmartShift.Core.IO;
using SmartShift.Processing;
using SmartShift.Processing.Batch;
using SmartShift.Processing.Documents;
using SmartShift.Processing.Images;
using SmartShift.Processing.Pdf.Preview;
using SmartShift.Windows.DragDrop;
using SmartShift.Windows.Shell;

namespace SmartShift.App;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<QueuedFileItem> _files = [];
    private readonly ObservableCollection<RadialActionItem> _radialItems = [];
    private QueuedFileItem? _selectedFileItem;
    private ActionRecommendation? _selectedAction;
    private CancellationTokenSource? _runCts;
    private DiagnosticsWindow? _diagnostics;
    private bool _busy;
    private bool _sidebarExpanded;
    private bool _updatingQuality;
    private bool _updatingPdfTarget;
    private string _pdfTargetUnit = "MB";
    private string _pdfQuality = "balanced";
    private string _pdfExportFormat = "png";
    private string _pdfExportDpi = "150";
    private string _pdfPptxMode = "pageAsImage";
    private bool _updatingPdfExportQuality;
    private bool _isInitialized;
    private readonly Queue<string> _pendingPaths = new();
    private string? _lastOutputPath;
    private Brush? _dropZoneIdleBorder;
    // Interactive Image Editor state
    private ImageEditSession? _imageEditSession;
    private SKRectI _cropPixelRect;
    private double? _targetCropAspectRatio;
    private CropHandle _activeDragHandle = CropHandle.None;
    private Point _dragStartPoint;
    private SKRectI _dragStartCropRect;
    private bool _isDrawingNewCrop;
    private ViewportBounds _editorViewport = new(0, 0, 0, 0, 1.0);
    private bool _updatingEditorResize;
    private readonly PdfPreviewCache _previewCache = new(maxEntries: 6);
    private PdfPreviewDocumentInfo? _previewDocInfo;
    private int _previewCurrentPage = 1;
    private double _previewZoomFactor = 1.0;
    private enum PreviewFitMode { None, FitPage, FitWidth }
    private PreviewFitMode _previewFitMode = PreviewFitMode.FitPage;
    private CancellationTokenSource? _previewRenderCts;
    // Batch Image Studio state
    private readonly ObservableCollection<BatchQueueItemViewModel> _batchFiles = [];
    // Multi-Image Document Builder state
    private readonly ObservableCollection<DocBuilderItemViewModel> _docBuilderFiles = [];
    private DocumentOutputMode _docBuilderMode = DocumentOutputMode.Pdf;
    private PageSizeOption _docPageSize = PageSizeOption.A4;
    private PageOrientationOption _docPageOrientation = PageOrientationOption.Portrait;
    private ImagePlacementOption _docPdfPlacement = ImagePlacementOption.Contain;
    private int _docPdfMarginPoints = 36;
    private string _docPdfBackgroundHex = "#FFFFFF";
    private SlideSizeOption _docSlideSize = SlideSizeOption.Widescreen16x9;
    private ImagePlacementOption _docPptxPlacement = ImagePlacementOption.Contain;
    private string _docPptxBackgroundHex = "#FFFFFF";
    private int _docBuilderPreviewIndex = 0;
    private bool _isDocBuilding;
    private CancellationTokenSource? _docBuilderCts;
    private string? _lastGeneratedDocumentPath;
    private CancellationTokenSource? _batchCts;
    private bool _isBatchRunning;
    private string _selectedBatchActionId = ActionIds.CompressImage;
    private string _selectedBatchTargetFormat = "webp";

    public MainWindow()
    {
        InitializeComponent();
        _isInitialized = true;
        FileList.ItemsSource = _files;
        RadialActionsControl.ItemsSource = _radialItems;
        ModernActionCardsControl.ItemsSource = _radialItems;
        BatchFileList.ItemsSource = _batchFiles;
        DocBuilderFileList.ItemsSource = _docBuilderFiles;

        Closed += (_, _) =>
        {
            _runCts?.Cancel();
            _previewRenderCts?.Cancel();
            _batchCts?.Cancel();
            _docBuilderCts?.Cancel();
            _diagnostics?.Close();
        };

        Loaded += (_, _) =>
        {
            _dropZoneIdleBorder = DropZone.BorderBrush;
            OpenOutputsBtn.ToolTip = $"Opens {App.Services.Outputs.RootDirectory} in Windows File Explorer";
            SettingsOutputDirBox.Text = App.Services.Outputs.RootDirectory;
            if (App.DiagnosticsRequested)
            {
                DiagnosticsButton.Visibility = Visibility.Visible;
                OpenDiagnostics();
            }
        };

        RefreshQueueChrome();
    }

    // ==========================================
    // DRAG AND DROP
    // ==========================================

    protected override void OnDragEnter(DragEventArgs e) => HandleDrag(e, preview: true);

    protected override void OnDragOver(DragEventArgs e) => HandleDrag(e, preview: true);

    protected override void OnDragLeave(DragEventArgs e)
    {
        ClearDropHighlight();
        base.OnDragLeave(e);
    }

    protected override void OnDrop(DragEventArgs e)
    {
        HandleDrag(e, preview: false);
        ClearDropHighlight();
        if (e.Data is null)
        {
            return;
        }

        var result = OleFileDropExtractor.Extract(e.Data);
        if (!result.HasFileDrop)
        {
            ShowBanner("That drop did not contain files. Originals were not changed.", error: true);
            return;
        }

        _ = IngestAsync(result.Inspection.Files.Select(f => f.Path).ToList());
    }

    private void HandleDrag(DragEventArgs e, bool preview)
    {
        var result = OleFileDropExtractor.Extract(e.Data);
        e.Effects = OleFileDropExtractor.SafeAllowedEffects(result.HasFileDrop);
        e.Handled = true;
        SetDropHighlight(result.HasFileDrop);
        _ = preview;
    }

    private void SetDropHighlight(bool canDrop)
    {
        DropZone.BorderBrush = (Brush)FindResource(canDrop ? "AccentBrush" : "WarnBrush");
        DropZone.BorderThickness = new Thickness(2);
    }

    private void ClearDropHighlight()
    {
        DropZone.BorderBrush = _dropZoneIdleBorder ?? (Brush)FindResource("BorderBrush");
        DropZone.BorderThickness = new Thickness(1);
    }

    private async Task IngestAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return;
        }

        if (_busy)
        {
            foreach (var path in paths)
            {
                _pendingPaths.Enqueue(path);
            }

            return;
        }

        _busy = true;
        LoadingOverlay.Visibility = Visibility.Visible;
        LoadingTitle.Text = "Inspecting files…";
        LoadingDetail.Text = "Reading file headers. Originals are never moved.";
        try
        {
            var classified = await Task.Run(() =>
            {
                var classifier = App.Services.Classifier;
                return paths.Select(classifier.Classify).ToList();
            }).ConfigureAwait(true);

            foreach (var file in classified)
            {
                App.Services.Queue.AddOrReplace(file);
                var existing = _files.FirstOrDefault(i => string.Equals(i.File.Path, file.Path, StringComparison.OrdinalIgnoreCase));
                if (existing is not null)
                {
                    var index = _files.IndexOf(existing);
                    _files[index] = new QueuedFileItem(file);
                }
                else
                {
                    _files.Add(new QueuedFileItem(file));
                }
            }

            RefreshQueueChrome();
            FileList.SelectedItem = _files.LastOrDefault();
            var supported = classified.Count(c => c.IsSupportedCategory);
            ShowBanner($"Added {classified.Count} item(s). {supported} recognized. Originals were not modified.", error: false);
        }
        catch (Exception ex)
        {
            App.Logger.Error("queue", "Ingest failed.", ex);
            ShowBanner("Files could not be inspected. Nothing was modified.", error: true);
        }
        finally
        {
            _busy = false;
            LoadingOverlay.Visibility = Visibility.Collapsed;
            if (_pendingPaths.Count > 0)
            {
                var next = _pendingPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                _pendingPaths.Clear();
                await IngestAsync(next).ConfigureAwait(true);
            }
        }
    }

    // ==========================================
    // FILE QUEUE SELECTION & RADIAL WORKSPACE
    // ==========================================

    private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ClosePdfPreview();
        _selectedFileItem = FileList.SelectedItem as QueuedFileItem;
        if (_selectedFileItem is null)
        {
            EmptyRadialPlaceholder.Visibility = Visibility.Visible;
            RadialMenuCanvas.Visibility = Visibility.Collapsed;
            if (CenterFileHeaderBar is not null)
            {
                CenterFileHeaderBar.Visibility = Visibility.Collapsed;
            }
            NoActionPlaceholder.Visibility = Visibility.Visible;
            ActionConfigPanel.Visibility = Visibility.Collapsed;
            _radialItems.Clear();
            _selectedAction = null;
            return;
        }

        // Display radial workspace
        EmptyRadialPlaceholder.Visibility = Visibility.Collapsed;
        RadialMenuCanvas.Visibility = Visibility.Visible;
        if (CenterFileHeaderBar is not null)
        {
            CenterFileHeaderBar.Visibility = Visibility.Visible;
        }

        // Update Center Preview
        LoadCenterPreview(_selectedFileItem.File);

        // Populate Radial Actions
        PopulateRadialActions(_selectedFileItem.File);
    }

    private void LoadCenterPreview(ClassifiedFile file)
    {
        CenterPreviewImage.Source = null;

        if (file.Format is DetectedFormat.Png or DetectedFormat.Jpeg or DetectedFormat.Webp && File.Exists(file.Path))
        {
            CenterDocumentPlaceholder.Visibility = Visibility.Collapsed;
            CenterPreviewImage.Visibility = Visibility.Visible;

            if (file.LengthBytes <= 25 * 1024 * 1024)
            {
                try
                {
                    using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var copy = new MemoryStream();
                    stream.CopyTo(copy);
                    copy.Position = 0;

                    var image = new BitmapImage();
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.DecodePixelWidth = 520;
                    image.StreamSource = copy;
                    image.EndInit();
                    image.Freeze();

                    CenterPreviewImage.Source = image;
                }
                catch
                {
                    // Fall back
                    CenterPreviewImage.Visibility = Visibility.Collapsed;
                    CenterDocumentPlaceholder.Visibility = Visibility.Visible;
                    CenterDocFormat.Text = file.DisplayType;
                    CenterDocVersion.Text = "Preview unavailable";
                }
            }

            CenterPreviewLabel.Text = $"{file.DisplayType} · {FileSizeFormatter.Format(file.LengthBytes)}";
        }
        else if (file.Format == DetectedFormat.Pdf)
        {
            CenterPreviewImage.Visibility = Visibility.Collapsed;
            CenterDocumentPlaceholder.Visibility = Visibility.Visible;
            CenterDocFormat.Text = "PDF Document";
            CenterDocVersion.Text = file.PdfVersion ?? "Standard PDF";
            CenterPreviewLabel.Text = $"PDF · {FileSizeFormatter.Format(file.LengthBytes)}";

            if (_selectedFileItem?.Thumbnail is not null)
            {
                CenterPreviewImage.Source = _selectedFileItem.Thumbnail;
                CenterPreviewImage.Visibility = Visibility.Visible;
                CenterDocumentPlaceholder.Visibility = Visibility.Collapsed;
            }
        }
        else
        {
            CenterPreviewImage.Visibility = Visibility.Collapsed;
            CenterDocumentPlaceholder.Visibility = Visibility.Visible;
            CenterDocFormat.Text = file.DisplayType;
            CenterDocVersion.Text = "Unsupported format";
            CenterPreviewLabel.Text = FileSizeFormatter.Format(file.LengthBytes);

            if (_selectedFileItem?.Thumbnail is not null)
            {
                CenterPreviewImage.Source = _selectedFileItem.Thumbnail;
                CenterPreviewImage.Visibility = Visibility.Visible;
                CenterDocumentPlaceholder.Visibility = Visibility.Collapsed;
            }
        }

        // Sync Smart Header Details
        if (HeroFileNameText is not null)
        {
            HeroFileNameText.Text = Path.GetFileName(file.Path);
        }

        if (HeroFormatText is not null)
        {
            HeroFormatText.Text = file.Format switch
            {
                DetectedFormat.Pdf => file.PdfVersion ?? "PDF",
                _ => file.DisplayType.ToUpperInvariant()
            };
        }

        if (HeroFileSizeText is not null)
        {
            HeroFileSizeText.Text = FileSizeFormatter.Format(file.LengthBytes);
        }

        if (HeroDimensionsText is not null)
        {
            HeroDimensionsText.Text = !string.IsNullOrWhiteSpace(_selectedFileItem?.DimensionsText)
                ? _selectedFileItem.DimensionsText
                : "";
        }

        if (HeroOpenPdfBtn is not null)
        {
            HeroOpenPdfBtn.Visibility = file.Format == DetectedFormat.Pdf ? Visibility.Visible : Visibility.Collapsed;
        }

        if (HeroEditImageBtn is not null)
        {
            HeroEditImageBtn.Visibility = (file.Format is DetectedFormat.Png or DetectedFormat.Jpeg or DetectedFormat.Webp)
                ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void HeroOpenPdf_Click(object sender, RoutedEventArgs e)
    {
        var previewAction = _radialItems.FirstOrDefault(r => r.ActionId == ActionIds.PreviewPdf);
        if (previewAction is not null)
        {
            SelectRadialAction(previewAction);
        }
    }

    private void HeroEditImage_Click(object sender, RoutedEventArgs e)
    {
        var editAction = _radialItems.FirstOrDefault(r => r.ActionId == ActionIds.EditImage);
        if (editAction is not null)
        {
            SelectRadialAction(editAction);
        }
        else
        {
            OpenImageEditor();
        }
    }

    private void OpenEditorFromResize_Click(object sender, RoutedEventArgs e) => OpenImageEditor();

    private void OpenEditorFromRightPanel_Click(object sender, RoutedEventArgs e) => OpenImageEditor();

    private void PopulateRadialActions(ClassifiedFile file)
    {
        var recs = App.Services.Recommendations.Recommend(file);
        _radialItems.Clear();

        for (int i = 0; i < recs.Count; i++)
        {
            _radialItems.Add(new RadialActionItem(recs[i], i, recs.Count));
        }

        UpdateRadialLayout();

        // Auto-select first available action (or preserve previous action if applicable)
        var previousActionId = _selectedAction?.ActionId;
        var toSelect = _radialItems.FirstOrDefault(r => r.ActionId == previousActionId && r.IsAvailable)
                       ?? _radialItems.FirstOrDefault(r => r.IsAvailable)
                       ?? _radialItems.FirstOrDefault();

        if (toSelect is not null)
        {
            SelectRadialAction(toSelect);
        }
        else
        {
            NoActionPlaceholder.Visibility = Visibility.Visible;
            ActionConfigPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void RadialMenuCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateRadialLayout();
    }

    private void UpdateRadialLayout()
    {
        if (RadialMenuCanvas is null || _radialItems.Count == 0)
        {
            return;
        }

        double width = RadialMenuCanvas.ActualWidth;
        double height = RadialMenuCanvas.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        double cx = width / 2.0;
        double cy = height / 2.0;
        double minDim = Math.Min(width, height);

        // Petal wheel sizing based on viewport
        double outerRadius = Math.Clamp((minDim / 2.0) - 22.0, 150.0, 215.0);
        double innerRadius = outerRadius * 0.52;
        double centerSize = (innerRadius * 2.0) - 10.0;

        // Center hub fits cleanly in inner hole
        CenterPreviewContainer.Width = centerSize;
        CenterPreviewContainer.Height = centerSize;
        CenterPreviewContainer.CornerRadius = new CornerRadius(centerSize / 2.0);
        Canvas.SetLeft(CenterPreviewContainer, cx - (centerSize / 2.0));
        Canvas.SetTop(CenterPreviewContainer, cy - (centerSize / 2.0));

        // Ambient halo behind center core
        if (CenterCoreHalo is not null)
        {
            double haloSize = (outerRadius * 2.0) + 40.0;
            CenterCoreHalo.Width = haloSize;
            CenterCoreHalo.Height = haloSize;
            Canvas.SetLeft(CenterCoreHalo, cx - (haloSize / 2.0));
            Canvas.SetTop(CenterCoreHalo, cy - (haloSize / 2.0));
        }

        // Outer framing ring
        if (OrbitalRing is not null)
        {
            OrbitalRing.Width = (outerRadius * 2.0) + 8.0;
            OrbitalRing.Height = (outerRadius * 2.0) + 8.0;
            Canvas.SetLeft(OrbitalRing, cx - (outerRadius + 4.0));
            Canvas.SetTop(OrbitalRing, cy - (outerRadius + 4.0));
        }

        // Inner hub rim
        if (InnerOrbitalRing is not null)
        {
            InnerOrbitalRing.Width = innerRadius * 2.0;
            InnerOrbitalRing.Height = innerRadius * 2.0;
            Canvas.SetLeft(InnerOrbitalRing, cx - innerRadius);
            Canvas.SetTop(InnerOrbitalRing, cy - innerRadius);
        }

        int count = _radialItems.Count;
        double sliceAngle = 360.0 / count;
        double gapDeg = count switch
        {
            <= 3 => 5.0,
            <= 6 => 4.0,
            _ => 3.0
        };
        double petalSpan = sliceAngle - gapDeg;
        double midRadius = (innerRadius + outerRadius) / 2.0;

        for (int i = 0; i < count; i++)
        {
            var item = _radialItems[i];
            double centerAngle = -90.0 + (i * sliceAngle);
            double startAngle = centerAngle - (petalSpan / 2.0);
            double endAngle = centerAngle + (petalSpan / 2.0);

            item.PetalGeometry = PetalWheelGeometry.CreatePetal(cx, cy, innerRadius, outerRadius, startAngle, endAngle);
            item.CenterX = cx;
            item.CenterY = cy;

            double rad = centerAngle * (Math.PI / 180.0);
            double lx = cx + (midRadius * Math.Cos(rad));
            double ly = cy + (midRadius * Math.Sin(rad));

            // Center the 84px label block
            item.X = lx - 42.0;
            item.Y = ly - 20.0;
        }

        UpdateSmartConnectingBeam(cx, cy);
    }

    private void UpdateSmartConnectingBeam(double cx, double cy)
    {
        if (SmartConnectingBeam is null)
        {
            return;
        }

        SmartConnectingBeam.Visibility = Visibility.Collapsed;
    }

    // ==========================================
    // ACTION SELECTION & CONFIGURATION
    // ==========================================

    private void RadialAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: RadialActionItem item })
        {
            SelectRadialAction(item);
        }
    }

    private void SelectRadialAction(RadialActionItem item)
    {
        foreach (var r in _radialItems)
        {
            r.IsSelected = (r == item);
        }

        if (CenterActiveActionText is not null)
        {
            CenterActiveActionText.Text = item.FullTitle;
        }

        if (RadialMenuCanvas is not null && RadialMenuCanvas.ActualWidth > 0 && RadialMenuCanvas.ActualHeight > 0)
        {
            UpdateSmartConnectingBeam(RadialMenuCanvas.ActualWidth / 2.0, RadialMenuCanvas.ActualHeight / 2.0);
        }

        _selectedAction = item.Recommendation;
        NoActionPlaceholder.Visibility = Visibility.Collapsed;
        ActionConfigPanel.Visibility = Visibility.Visible;

        // Header info
        ActionTitleText.Text = item.FullTitle;
        ActionGoalText.Text = item.GoalTitle;
        ActionDescText.Text = item.Description;
        ActionAvailabilityText.Text = item.Badge;
        ActionAvailabilityBadge.Background = item.BadgeBrush;
        ActionAvailabilityText.Foreground = item.BadgeForeground;
        RunButton.IsEnabled = item.IsAvailable && !_busy;

        // Clear errors & hide previous result
        ValidationText.Visibility = Visibility.Collapsed;
        ResultCardHost.Visibility = Visibility.Collapsed;

        // Hide all option subviews first
        CompressOptionsView.Visibility = Visibility.Collapsed;
        WebpOptionsView.Visibility = Visibility.Collapsed;
        ResizeOptionsView.Visibility = Visibility.Collapsed;
        MetadataOptionsView.Visibility = Visibility.Collapsed;
        InfoOptionsView.Visibility = Visibility.Collapsed;
        PdfCompressOptionsView.Visibility = Visibility.Collapsed;
        PdfExportOptionsView.Visibility = Visibility.Collapsed;
        PdfTextOptionsView.Visibility = Visibility.Collapsed;
        PdfDocxOptionsView.Visibility = Visibility.Collapsed;
        PdfPptxOptionsView.Visibility = Visibility.Collapsed;
        PdfPreviewOptionsView.Visibility = Visibility.Collapsed;
        EditImageOptionsView.Visibility = Visibility.Collapsed;

        if (item.ActionId != ActionIds.PreviewPdf)
        {
            ClosePdfPreview();
        }

        if (item.ActionId != ActionIds.EditImage)
        {
            CloseImageEditor();
        }

        if (_selectedFileItem is null)
        {
            return;
        }

        var file = _selectedFileItem.File;

        switch (item.ActionId)
        {
            case ActionIds.CompressImage:
                CompressOptionsView.Visibility = Visibility.Visible;
                CompressFormatNote.Text = file.Format switch
                {
                    DetectedFormat.Jpeg => "JPEG uses lossy compression. Transparency is not supported in JPEG.",
                    DetectedFormat.Png => "PNG compression is strictly lossless. Quality maps to zlib effort levels (1–9). Alpha transparency is preserved.",
                    DetectedFormat.Webp => "WebP quality 1–99 is lossy. Quality 100 is lossless. Alpha transparency is preserved.",
                    _ => "Image re-encoding."
                };
                break;

            case ActionIds.ConvertToWebp:
                WebpOptionsView.Visibility = Visibility.Visible;
                break;

            case ActionIds.ResizeImage:
                ResizeOptionsView.Visibility = Visibility.Visible;
                var w = file.PixelWidth ?? 1920;
                var h = file.PixelHeight ?? 1080;
                ResizeWidthBox.Text = w.ToString();
                ResizeHeightBox.Text = h.ToString();
                ResizeKeepAspectCheck.IsChecked = true;
                UpdateResizeHeightState();
                break;

            case ActionIds.RemoveImageMetadata:
                MetadataOptionsView.Visibility = Visibility.Visible;
                break;

            case ActionIds.ShowImageInfo:
            case ActionIds.ShowPdfInfo:
                InfoOptionsView.Visibility = Visibility.Visible;
                InfoFormatText.Text = file.DisplayType;
                InfoDimensionsText.Text = _selectedFileItem.DimensionsText;
                InfoSizeText.Text = $"{_selectedFileItem.SizeText} ({file.LengthBytes:N0} bytes)";
                InfoModifiedText.Text = file.LastWriteTimeUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "Unknown";
                InfoStatusText.Text = file.Status;
                break;

            case ActionIds.CompressPdf:
                PdfCompressOptionsView.Visibility = Visibility.Visible;
                ValidatePdfTargetInput();
                break;

            case ActionIds.ExportPdfPages:
                PdfExportOptionsView.Visibility = Visibility.Visible;
                ValidatePdfExportInput();
                break;

            case ActionIds.ExtractPdfText:
                PdfTextOptionsView.Visibility = Visibility.Visible;
                ValidatePdfTextInput();
                break;

            case ActionIds.ConvertPdfToDocx:
                PdfDocxOptionsView.Visibility = Visibility.Visible;
                ValidatePdfDocxInput();
                break;

            case ActionIds.ConvertPdfToPptx:
                PdfPptxOptionsView.Visibility = Visibility.Visible;
                ValidatePdfPptxInput();
                break;

            case ActionIds.PreviewPdf:
                PdfPreviewOptionsView.Visibility = Visibility.Visible;
                _ = OpenPdfPreviewAsync();
                break;

            case ActionIds.EditImage:
                EditImageOptionsView.Visibility = Visibility.Visible;
                OpenImageEditor();
                break;
        }
    }

    // ==========================================
    // ACTION CONTROLS & PRESETS
    // ==========================================

    private void CompressQualitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_isInitialized || _updatingQuality || CompressQualityBox is null) return;
        _updatingQuality = true;
        CompressQualityBox.Text = ((int)CompressQualitySlider.Value).ToString();
        _updatingQuality = false;
    }

    private void CompressQualityBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_isInitialized || _updatingQuality || CompressQualitySlider is null) return;
        if (int.TryParse(CompressQualityBox.Text, out var val) && val >= 1 && val <= 100)
        {
            _updatingQuality = true;
            CompressQualitySlider.Value = val;
            _updatingQuality = false;
        }
    }

    private void WebpQualitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_isInitialized || _updatingQuality || WebpQualityBox is null) return;
        _updatingQuality = true;
        WebpQualityBox.Text = ((int)WebpQualitySlider.Value).ToString();
        _updatingQuality = false;
    }

    private void WebpQualityBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_isInitialized || _updatingQuality || WebpQualitySlider is null) return;
        if (int.TryParse(WebpQualityBox.Text, out var val) && val >= 1 && val <= 100)
        {
            _updatingQuality = true;
            WebpQualitySlider.Value = val;
            _updatingQuality = false;
        }
    }

    private void ResizeKeepAspectCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized || ResizeHeightBox is null) return;
        UpdateResizeHeightState();
    }

    private void UpdateResizeHeightState()
    {
        var keepAspect = ResizeKeepAspectCheck.IsChecked == true;
        ResizeHeightBox.IsEnabled = !keepAspect;
        ResizeHeightBox.Opacity = keepAspect ? 0.6 : 1.0;
        if (keepAspect)
        {
            RecalculateProportionalHeight();
        }
    }

    private void ResizeWidthBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_isInitialized || ResizeKeepAspectCheck is null) return;
        if (ResizeKeepAspectCheck.IsChecked == true)
        {
            RecalculateProportionalHeight();
        }
    }

    private void RecalculateProportionalHeight()
    {
        if (_selectedFileItem is null) return;
        var file = _selectedFileItem.File;
        if (int.TryParse(ResizeWidthBox.Text, out int w) && w > 0 &&
            file.PixelWidth is int srcW && srcW > 0 &&
            file.PixelHeight is int srcH && srcH > 0)
        {
            int h = Math.Max(1, (int)Math.Round(w * (srcH / (double)srcW)));
            ResizeHeightBox.Text = h.ToString();
        }
    }

    private void Preset25_Click(object sender, RoutedEventArgs e) => ApplyResizePreset(0.25);
    private void Preset50_Click(object sender, RoutedEventArgs e) => ApplyResizePreset(0.5);
    private void Preset75_Click(object sender, RoutedEventArgs e) => ApplyResizePreset(0.75);

    private void ApplyResizePreset(double scale)
    {
        if (_selectedFileItem?.File.PixelWidth is int srcW && srcW > 0)
        {
            int w = Math.Max(1, (int)Math.Round(srcW * scale));
            ResizeWidthBox.Text = w.ToString();
        }
    }

    private void Preset1080p_Click(object sender, RoutedEventArgs e)
    {
        ResizeWidthBox.Text = "1920";
        if (ResizeKeepAspectCheck.IsChecked != true)
        {
            ResizeHeightBox.Text = "1080";
        }
    }

    private void Preset720p_Click(object sender, RoutedEventArgs e)
    {
        ResizeWidthBox.Text = "1280";
        if (ResizeKeepAspectCheck.IsChecked != true)
        {
            ResizeHeightBox.Text = "720";
        }
    }

    // ==========================================
    // PDF TARGET-SIZE & IMAGE QUALITY SETTINGS
    // ==========================================

    private void PdfPreset500Kb_Click(object sender, RoutedEventArgs e) => SetPdfTarget("500", "KB", PdfPreset500Kb);
    private void PdfPreset1Mb_Click(object sender, RoutedEventArgs e) => SetPdfTarget("1", "MB", PdfPreset1Mb);
    private void PdfPreset2Mb_Click(object sender, RoutedEventArgs e) => SetPdfTarget("2", "MB", PdfPreset2Mb);
    private void PdfPreset5Mb_Click(object sender, RoutedEventArgs e) => SetPdfTarget("5", "MB", PdfPreset5Mb);

    private void PdfPresetCustom_Click(object sender, RoutedEventArgs e)
    {
        HighlightPdfPresetButton(PdfPresetCustom);
        PdfTargetSizeBox.Focus();
        PdfTargetSizeBox.SelectAll();
    }

    private void SetPdfTarget(string size, string unit, Button presetButton)
    {
        _updatingPdfTarget = true;
        try
        {
            PdfTargetSizeBox.Text = size;
            _pdfTargetUnit = unit;
            UpdatePdfUnitButtons();
            HighlightPdfPresetButton(presetButton);
            ValidatePdfTargetInput();
        }
        finally
        {
            _updatingPdfTarget = false;
        }
    }

    private void PdfUnitMb_Click(object sender, RoutedEventArgs e)
    {
        _pdfTargetUnit = "MB";
        UpdatePdfUnitButtons();
        ValidatePdfTargetInput();
        SyncActivePdfPreset();
    }

    private void PdfUnitKb_Click(object sender, RoutedEventArgs e)
    {
        _pdfTargetUnit = "KB";
        UpdatePdfUnitButtons();
        ValidatePdfTargetInput();
        SyncActivePdfPreset();
    }

    private void UpdatePdfUnitButtons()
    {
        if (!_isInitialized || PdfUnitMbBtn is null || PdfUnitKbBtn is null) return;
        var isMb = string.Equals(_pdfTargetUnit, "MB", StringComparison.OrdinalIgnoreCase);
        PdfUnitMbBtn.Style = (Style)FindResource(isMb ? "PrimaryButton" : "SecondaryButton");
        PdfUnitKbBtn.Style = (Style)FindResource(!isMb ? "PrimaryButton" : "SecondaryButton");
    }

    private void PdfTargetSizeBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_isInitialized || _updatingPdfTarget || PdfTargetSizeBox is null) return;
        ValidatePdfTargetInput();
        SyncActivePdfPreset();
    }

    private bool ValidatePdfTargetInput()
    {
        if (!_isInitialized || PdfTargetSizeBox is null || PdfTargetValidationHint is null) return false;

        var text = PdfTargetSizeBox.Text.Trim();
        if (double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var size) && size > 0 && !double.IsNaN(size) && !double.IsInfinity(size))
        {
            var isMb = string.Equals(_pdfTargetUnit, "MB", StringComparison.OrdinalIgnoreCase);
            var requestedMb = isMb ? size : size / 1024.0;
            if (requestedMb <= 1024.0)
            {
                var bytes = isMb ? (long)Math.Round(size * 1024.0 * 1024.0) : (long)Math.Round(size * 1024.0);
                PdfTargetValidationHint.Text = $"Target goal: {size} {_pdfTargetUnit} ({bytes:N0} bytes)";
                PdfTargetValidationHint.Foreground = (Brush)FindResource("MutedBrush");
                if (RunButton is not null && !_busy && _selectedAction?.IsAvailable == true)
                {
                    RunButton.IsEnabled = true;
                }
                return true;
            }
        }

        PdfTargetValidationHint.Text = "Please enter a positive target size (1 KB to 1024 MB).";
        PdfTargetValidationHint.Foreground = (Brush)FindResource("WarnBrush");
        if (RunButton is not null && _selectedAction?.ActionId == ActionIds.CompressPdf)
        {
            RunButton.IsEnabled = false;
        }
        return false;
    }

    private void SyncActivePdfPreset()
    {
        if (!_isInitialized || _updatingPdfTarget || PdfTargetSizeBox is null) return;
        var text = PdfTargetSizeBox.Text.Trim();
        var isMb = string.Equals(_pdfTargetUnit, "MB", StringComparison.OrdinalIgnoreCase);

        if (isMb && text == "1") HighlightPdfPresetButton(PdfPreset1Mb);
        else if (isMb && text == "2") HighlightPdfPresetButton(PdfPreset2Mb);
        else if (isMb && text == "5") HighlightPdfPresetButton(PdfPreset5Mb);
        else if (!isMb && text == "500") HighlightPdfPresetButton(PdfPreset500Kb);
        else HighlightPdfPresetButton(PdfPresetCustom);
    }

    private void HighlightPdfPresetButton(Button? active)
    {
        if (!_isInitialized) return;
        var buttons = new[] { PdfPreset500Kb, PdfPreset1Mb, PdfPreset2Mb, PdfPreset5Mb, PdfPresetCustom };
        foreach (var b in buttons)
        {
            if (b is null) continue;
            b.Style = (Style)FindResource(b == active ? "PrimaryButton" : "SecondaryButton");
        }
    }

    private void PdfQuality_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string policy })
        {
            _pdfQuality = policy;
            HighlightPdfQualityButton(policy);
        }
    }

    private void HighlightPdfQualityButton(string policy)
    {
        if (!_isInitialized) return;
        var mapping = new (Button? Btn, string Key)[]
        {
            (PdfQualityBalancedBtn, "balanced"),
            (PdfQualityBestBtn, "best"),
            (PdfQualitySmallestBtn, "smallest"),
            (PdfQualityOffBtn, "off")
        };

        foreach (var (btn, key) in mapping)
        {
            if (btn is null) continue;
            btn.Style = (Style)FindResource(string.Equals(key, policy, StringComparison.OrdinalIgnoreCase) ? "PrimaryButton" : "SecondaryButton");
        }
    }

    // ==========================================
    // PDF PAGE EXPORT & TEXT EXTRACTION SETTINGS
    // ==========================================

    private void PdfExportFormat_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string fmt })
        {
            _pdfExportFormat = fmt;
            PdfExportPngBtn.Style = (Style)FindResource(fmt == "png" ? "PrimaryButton" : "SecondaryButton");
            PdfExportJpegBtn.Style = (Style)FindResource(fmt == "jpeg" ? "PrimaryButton" : "SecondaryButton");
            PdfExportQualityHost.Visibility = fmt == "jpeg" ? Visibility.Visible : Visibility.Collapsed;
            ValidatePdfExportInput();
        }
    }

    private void PdfExportQualitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_isInitialized || _updatingPdfExportQuality || PdfExportQualityBox is null) return;
        _updatingPdfExportQuality = true;
        PdfExportQualityBox.Text = ((int)PdfExportQualitySlider.Value).ToString();
        _updatingPdfExportQuality = false;
        ValidatePdfExportInput();
    }

    private void PdfExportQualityBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_isInitialized || _updatingPdfExportQuality || PdfExportQualitySlider is null) return;
        if (int.TryParse(PdfExportQualityBox.Text, out int q) && q >= 1 && q <= 100)
        {
            _updatingPdfExportQuality = true;
            PdfExportQualitySlider.Value = q;
            _updatingPdfExportQuality = false;
        }
        ValidatePdfExportInput();
    }

    private void PdfExportDpi_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string dpi })
        {
            _pdfExportDpi = dpi;
            PdfExportDpi100Btn.Style = (Style)FindResource(dpi == "100" ? "PrimaryButton" : "SecondaryButton");
            PdfExportDpi150Btn.Style = (Style)FindResource(dpi == "150" ? "PrimaryButton" : "SecondaryButton");
            PdfExportDpi300Btn.Style = (Style)FindResource(dpi == "300" ? "PrimaryButton" : "SecondaryButton");
            ValidatePdfExportInput();
        }
    }

    private void PdfExportRangeBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ValidatePdfExportInput();
    }

    private void PdfTextRangeBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ValidatePdfTextInput();
    }

    private bool ValidatePdfExportInput()
    {
        if (!_isInitialized || PdfExportRangeBox is null) return true;

        var rangeText = PdfExportRangeBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(rangeText))
        {
            rangeText = "all";
        }

        if (!PageRangeParser.TryValidateSyntax(rangeText, out var rangeError))
        {
            PdfExportRangeHint.Text = rangeError ?? "Invalid page range.";
            PdfExportRangeHint.Foreground = (Brush)FindResource("DangerBrush");
            if (RunButton is not null && _selectedAction?.ActionId == ActionIds.ExportPdfPages)
            {
                RunButton.IsEnabled = false;
            }
            return false;
        }

        PdfExportRangeHint.Text = "Specify 'all', a single page (e.g. 1), or ranges (e.g. 1-5, 8).";
        PdfExportRangeHint.Foreground = (Brush)FindResource("MutedBrush");

        // High page count / resolution warning
        if (PdfExportWarningText is not null)
        {
            if (_pdfExportDpi == "300")
            {
                PdfExportWarningText.Text = "Warning: 300 DPI exports high-resolution images (~25-35 MB uncompressed RAM per page). High page counts may take longer.";
                PdfExportWarningText.Visibility = Visibility.Visible;
            }
            else
            {
                PdfExportWarningText.Visibility = Visibility.Collapsed;
            }
        }

        if (RunButton is not null && !_busy && _selectedAction?.IsAvailable == true && _selectedAction.ActionId == ActionIds.ExportPdfPages)
        {
            RunButton.IsEnabled = true;
        }

        return true;
    }

    private bool ValidatePdfTextInput()
    {
        if (!_isInitialized || PdfTextRangeBox is null) return true;

        var rangeText = PdfTextRangeBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(rangeText))
        {
            rangeText = "all";
        }

        if (!PageRangeParser.TryValidateSyntax(rangeText, out var rangeError))
        {
            PdfTextRangeHint.Text = rangeError ?? "Invalid page range.";
            PdfTextRangeHint.Foreground = (Brush)FindResource("DangerBrush");
            if (RunButton is not null && _selectedAction?.ActionId == ActionIds.ExtractPdfText)
            {
                RunButton.IsEnabled = false;
            }
            return false;
        }

        PdfTextRangeHint.Text = "Specify 'all', a single page (e.g. 1), or ranges (e.g. 1-5, 8).";
        PdfTextRangeHint.Foreground = (Brush)FindResource("MutedBrush");

        if (RunButton is not null && !_busy && _selectedAction?.IsAvailable == true && _selectedAction.ActionId == ActionIds.ExtractPdfText)
        {
            RunButton.IsEnabled = true;
        }

        return true;
    }

    private void PdfPptxMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string mode })
        {
            _pdfPptxMode = mode;
            var isImage = mode == "pageAsImage";
            PdfPptxModeImageBtn.Style = (Style)FindResource(isImage ? "PrimaryButton" : "SecondaryButton");
            PdfPptxModeEditableBtn.Style = (Style)FindResource(!isImage ? "PrimaryButton" : "SecondaryButton");
            PdfPptxModeExplanation.Text = isImage
                ? "Page-as-Image: Best for presentations, brochures, and visual fidelity. Each slide is an exact high-resolution rendering of the original PDF page."
                : "Editable: Reconstructs text boxes, paragraphs, and picture shapes as editable PowerPoint elements. Layout fidelity may vary from original PDF.";
            ValidatePdfPptxInput();
        }
    }

    private void PdfDocxRangeBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ValidatePdfDocxInput();
    }

    private void PdfPptxRangeBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ValidatePdfPptxInput();
    }

    private bool ValidatePdfDocxInput()
    {
        if (!_isInitialized || PdfDocxRangeBox is null) return true;

        var rangeText = PdfDocxRangeBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(rangeText))
        {
            rangeText = "all";
        }

        if (!PageRangeParser.TryValidateSyntax(rangeText, out var rangeError))
        {
            PdfDocxRangeHint.Text = rangeError ?? "Invalid page range.";
            PdfDocxRangeHint.Foreground = (Brush)FindResource("DangerBrush");
            if (RunButton is not null && _selectedAction?.ActionId == ActionIds.ConvertPdfToDocx)
            {
                RunButton.IsEnabled = false;
            }
            return false;
        }

        PdfDocxRangeHint.Text = "Specify 'all', a single page (e.g. 1), or ranges (e.g. 1-5, 8).";
        PdfDocxRangeHint.Foreground = (Brush)FindResource("MutedBrush");

        if (RunButton is not null && !_busy && _selectedAction?.IsAvailable == true && _selectedAction.ActionId == ActionIds.ConvertPdfToDocx)
        {
            RunButton.IsEnabled = true;
        }

        return true;
    }

    private bool ValidatePdfPptxInput()
    {
        if (!_isInitialized || PdfPptxRangeBox is null) return true;

        var rangeText = PdfPptxRangeBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(rangeText))
        {
            rangeText = "all";
        }

        if (!PageRangeParser.TryValidateSyntax(rangeText, out var rangeError))
        {
            PdfPptxRangeHint.Text = rangeError ?? "Invalid page range.";
            PdfPptxRangeHint.Foreground = (Brush)FindResource("DangerBrush");
            if (RunButton is not null && _selectedAction?.ActionId == ActionIds.ConvertPdfToPptx)
            {
                RunButton.IsEnabled = false;
            }
            return false;
        }

        PdfPptxRangeHint.Text = "Specify 'all', a single page (e.g. 1), or ranges (e.g. 1-5, 8).";
        PdfPptxRangeHint.Foreground = (Brush)FindResource("MutedBrush");

        if (RunButton is not null && !_busy && _selectedAction?.IsAvailable == true && _selectedAction.ActionId == ActionIds.ConvertPdfToPptx)
        {
            RunButton.IsEnabled = true;
        }

        return true;
    }

    // ==========================================
    // ACTION EXECUTION & RESULTS
    // ==========================================

    private async void RunAction_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedAction is null || _selectedFileItem is null)
        {
            return;
        }

        var values = GatherOptions(_selectedAction.ActionId);
        var errors = ActionOptionValidator.Validate(_selectedAction.ActionId, values);
        if (errors.Count > 0)
        {
            ValidationText.Text = string.Join(" ", errors);
            ValidationText.Visibility = Visibility.Visible;
            return;
        }

        ValidationText.Visibility = Visibility.Collapsed;

        if (_selectedAction.ActionId == ActionIds.PreviewPdf)
        {
            await OpenPdfPreviewAsync();
            return;
        }

        if (_selectedAction.ActionId == ActionIds.EditImage)
        {
            OpenImageEditor();
            return;
        }

        var processor = App.Services.Processors.Find(_selectedAction.ActionId);
        if (processor is null)
        {
            ShowBanner("No processor is registered for this action.", error: true);
            return;
        }

        _runCts?.Cancel();
        _runCts = new CancellationTokenSource();
        var token = _runCts.Token;

        _busy = true;
        RunButton.IsEnabled = false;
        ProgressHost.Visibility = Visibility.Visible;
        ProgressBarCtrl.IsIndeterminate = true;
        ProgressText.Text = "Processing…";
        ResultCardHost.Visibility = Visibility.Collapsed;

        try
        {
            var request = new ProcessingRequest(
                _selectedFileItem.File,
                _selectedAction.ActionId,
                new ProcessingOptions(values),
                App.Services.Outputs.RootDirectory);

            var progress = new Progress<ProcessingProgress>(p =>
            {
                Dispatcher.InvokeAsync(() =>
                {
                    ProgressBarCtrl.IsIndeterminate = false;
                    ProgressBarCtrl.Value = p.Percent;
                    ProgressText.Text = $"{p.Percent}% — {p.Message}";
                });
            });

            var result = await processor.ProcessAsync(request, progress, token).ConfigureAwait(true);
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
                if (summary.ResultState == ResultState.TargetNotReached)
                {
                    ShowBanner(summary.Status, error: false, warning: true);
                }
                else
                {
                    ShowBanner(summary.Status, error: false, warning: false);
                }
            }
            else if (!result.WasCancelled)
            {
                ShowBanner(result.Status, error: true, warning: false);
            }
        }
        catch (OperationCanceledException)
        {
            var cancelledSummary = new ProcessingResultSummary(
                "The operation was cancelled.",
                succeeded: false,
                wasCancelled: true,
                originalUnchanged: true,
                outputPath: null,
                originalSizeBytes: _selectedFileItem.File.LengthBytes,
                outputSizeBytes: 0,
                outputDimensions: null,
                outputFormat: null,
                encoderNote: null,
                metadataNote: null,
                sizeNote: null);

            DisplayResult(cancelledSummary);
            ShowBanner("Operation cancelled. Originals were not changed.", error: false, warning: true);
        }
        catch (Exception ex)
        {
            App.Logger.Error("process", "Action failed.", ex);
            var errorSummary = new ProcessingResultSummary(
                ex.Message,
                succeeded: false,
                wasCancelled: false,
                originalUnchanged: true,
                outputPath: null,
                originalSizeBytes: _selectedFileItem.File.LengthBytes,
                outputSizeBytes: 0,
                outputDimensions: null,
                outputFormat: null,
                encoderNote: null,
                metadataNote: null,
                sizeNote: null);

            DisplayResult(errorSummary);
            ShowBanner("The action failed. See details below.", error: true);
        }
        finally
        {
            _busy = false;
            ProgressHost.Visibility = Visibility.Collapsed;
            RunButton.IsEnabled = _selectedAction?.IsAvailable == true;
        }
    }

    private void DisplayResult(ProcessingResultSummary summary)
    {
        ResultCardHost.Visibility = Visibility.Visible;

        if (!string.IsNullOrEmpty(summary.OutputPath) && File.Exists(summary.OutputPath))
        {
            RecentFileNameText.Text = Path.GetFileName(summary.OutputPath);
            RecentFilePathText.Text = summary.OutputPath;
            RecentOpenBtn.IsEnabled = true;
            RecentFolderBtn.IsEnabled = true;
        }

        if (summary.Succeeded)
        {
            SuccessResultCard.Visibility = Visibility.Visible;
            ErrorResultCard.Visibility = Visibility.Collapsed;

            ResultStatusTitle.Text = summary.StateTitle;
            ResultBadgeText.Text = summary.StateBadgeText;

            if (summary.ResultState == ResultState.TargetNotReached)
            {
                SuccessResultCard.Background = (Brush)FindResource("WarnBgBrush");
                SuccessResultCard.BorderBrush = (Brush)FindResource("WarnBrush");
                ResultBadgeBorder.Background = (Brush)FindResource("WarnSoftBrush");
                ResultBadgeText.Foreground = (Brush)FindResource("WarnBrush");
                ResultStatusTitle.Foreground = (Brush)FindResource("WarnBrush");
                ResultStatusIcon.Data = (Geometry)FindResource("IconAlertGeometry");
                ResultStatusIcon.Fill = (Brush)FindResource("WarnBrush");
            }
            else
            {
                SuccessResultCard.Background = (Brush)FindResource("OkBgBrush");
                SuccessResultCard.BorderBrush = (Brush)FindResource("OkBrush");
                ResultBadgeBorder.Background = (Brush)FindResource("OkSoftBrush");
                ResultBadgeText.Foreground = (Brush)FindResource("OkBrush");
                ResultStatusTitle.Foreground = (Brush)FindResource("OkBrush");
                ResultStatusIcon.Data = (Geometry)FindResource("IconCheckGeometry");
                ResultStatusIcon.Fill = (Brush)FindResource("OkBrush");
            }

            if (summary.ActionId == ActionIds.ExportPdfPages ||
                summary.ActionId == ActionIds.ExtractPdfText ||
                summary.ActionId == ActionIds.ConvertPdfToDocx ||
                summary.ActionId == ActionIds.ConvertPdfToPptx)
            {
                SizeComparisonBlock.Visibility = Visibility.Collapsed;
                TargetDiagnosticsBox.Visibility = Visibility.Collapsed;
                ResultDimensionsLine.Text = !string.IsNullOrWhiteSpace(summary.OutputDimensions)
                    ? $"{summary.OutputDimensions} · {summary.OutputFormat}"
                    : (summary.OutputFormat ?? summary.Status);
                ResultNotesLine.Text = summary.SizeComparisonSummary;
            }
            else if (!string.IsNullOrWhiteSpace(summary.OutputPath))
            {
                SizeComparisonBlock.Visibility = Visibility.Visible;
                ResultOrigSize.Text = summary.OriginalSizeText;
                ResultOutputSize.Text = summary.OutputSizeText;
                ResultDeltaText.Text = summary.DeltaText;

                if (summary.IsIncrease)
                {
                    DeltaBadgeBorder.Background = (Brush)FindResource("WarnSoftBrush");
                    ResultDeltaText.Foreground = (Brush)FindResource("WarnBrush");
                }
                else
                {
                    DeltaBadgeBorder.Background = summary.ResultState == ResultState.TargetNotReached
                        ? (Brush)FindResource("WarnSoftBrush")
                        : (Brush)FindResource("OkSoftBrush");
                    ResultDeltaText.Foreground = summary.ResultState == ResultState.TargetNotReached
                        ? (Brush)FindResource("WarnBrush")
                        : (Brush)FindResource("OkBrush");
                }

                ResultDimensionsLine.Text = $"{summary.OutputDimensions ?? _selectedFileItem?.DimensionsText} · {summary.OutputFormat ?? _selectedFileItem?.Extension}";
                ResultNotesLine.Text = summary.SizeComparisonSummary;

                if (summary.TargetSizeBytes.HasValue)
                {
                    TargetDiagnosticsBox.Visibility = Visibility.Visible;
                    var metSuffix = summary.TargetReached == true ? " (met)" : " (not reached)";
                    TargetGoalLine.Text = $"Goal: {summary.TargetSizeText}{metSuffix} · Stage: {summary.StageApplied ?? "Lossless"}";
                    TargetDiagnosticLine.Text = summary.LimitsExplanation ?? summary.TargetStatusNote ?? "";
                }
                else
                {
                    TargetDiagnosticsBox.Visibility = Visibility.Collapsed;
                }
            }
            else
            {
                SizeComparisonBlock.Visibility = Visibility.Collapsed;
                TargetDiagnosticsBox.Visibility = Visibility.Collapsed;
                ResultDimensionsLine.Text = summary.Status;
                ResultNotesLine.Text = "Header inspection only. No new file written.";
            }
        }
        else if (summary.WasCancelled)
        {
            SuccessResultCard.Visibility = Visibility.Collapsed;
            ErrorResultCard.Visibility = Visibility.Visible;
            ErrorResultCard.Background = (Brush)FindResource("WarnBgBrush");
            ErrorResultCard.BorderBrush = (Brush)FindResource("WarnBrush");
            ErrorResultTitle.Text = summary.StateTitle;
            ErrorResultTitle.Foreground = (Brush)FindResource("WarnBrush");
            ErrorResultDetail.Text = "The operation was cancelled by the user. The original file was not modified.";
        }
        else
        {
            SuccessResultCard.Visibility = Visibility.Collapsed;
            ErrorResultCard.Visibility = Visibility.Visible;
            ErrorResultCard.Background = (Brush)FindResource("DangerBgBrush");
            ErrorResultCard.BorderBrush = (Brush)FindResource("DangerBrush");
            ErrorResultTitle.Text = summary.StateTitle;
            ErrorResultTitle.Foreground = (Brush)FindResource("DangerBrush");
            ErrorResultDetail.Text = summary.Status;
        }
    }

    private Dictionary<string, string> GatherOptions(string actionId)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        switch (actionId)
        {
            case ActionIds.CompressImage:
                values["quality"] = CompressQualityBox.Text;
                break;

            case ActionIds.ConvertToWebp:
                values["quality"] = WebpQualityBox.Text;
                break;

            case ActionIds.ResizeImage:
                values["width"] = ResizeWidthBox.Text;
                values["height"] = ResizeHeightBox.Text;
                values["keepAspect"] = (ResizeKeepAspectCheck.IsChecked == true).ToString();
                break;

            case ActionIds.CompressPdf:
                values["targetSize"] = PdfTargetSizeBox.Text.Trim();
                values["targetUnit"] = _pdfTargetUnit;
                values["imageQuality"] = _pdfQuality;
                break;

            case ActionIds.ExportPdfPages:
                values["format"] = _pdfExportFormat;
                values["dpi"] = _pdfExportDpi;
                values["quality"] = PdfExportQualityBox?.Text ?? "85";
                values["pageRange"] = string.IsNullOrWhiteSpace(PdfExportRangeBox?.Text) ? "all" : PdfExportRangeBox.Text.Trim();
                break;

            case ActionIds.ExtractPdfText:
                values["pageRange"] = string.IsNullOrWhiteSpace(PdfTextRangeBox?.Text) ? "all" : PdfTextRangeBox.Text.Trim();
                break;

            case ActionIds.ConvertPdfToDocx:
                values["pageRange"] = string.IsNullOrWhiteSpace(PdfDocxRangeBox?.Text) ? "all" : PdfDocxRangeBox.Text.Trim();
                break;

            case ActionIds.ConvertPdfToPptx:
                values["slideMode"] = _pdfPptxMode;
                values["pageRange"] = string.IsNullOrWhiteSpace(PdfPptxRangeBox?.Text) ? "all" : PdfPptxRangeBox.Text.Trim();
                break;
        }

        return values;
    }

    private void CancelAction_Click(object sender, RoutedEventArgs e)
    {
        ClosePdfPreview();

        if (_busy)
        {
            _runCts?.Cancel();
            ProgressText.Text = "Cancelling…";
            return;
        }

        // Deselect current action
        foreach (var r in _radialItems)
        {
            r.IsSelected = false;
        }

        _selectedAction = null;
        ActionConfigPanel.Visibility = Visibility.Collapsed;
        NoActionPlaceholder.Visibility = Visibility.Visible;
    }

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_lastOutputPath))
        {
            try
            {
                if (File.Exists(_lastOutputPath))
                {
                    ShellFolders.OpenFile(_lastOutputPath);
                }
                else if (Directory.Exists(_lastOutputPath))
                {
                    ShellFolders.OpenDirectory(_lastOutputPath);
                }
            }
            catch (Exception ex)
            {
                App.Logger.Error("ui", "Could not open result file or folder.", ex);
                ShowBanner("The file or folder could not be opened with the default viewer.", error: true);
            }
        }
    }

    private void RevealResult_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_lastOutputPath) || (!File.Exists(_lastOutputPath) && !Directory.Exists(_lastOutputPath)))
        {
            ShowBanner("No output file has been generated yet.", error: true);
            return;
        }

        try
        {
            ShellFolders.RevealInExplorer(_lastOutputPath);
        }
        catch (Exception ex)
        {
            App.Logger.Error("ui", "Could not reveal the output file.", ex);
            ShowBanner("The result could not be shown in File Explorer.", error: true);
        }
    }

    private void OpenOutputs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            App.Services.Outputs.EnsureRoot();
            ShellFolders.OpenDirectory(App.Services.Outputs.RootDirectory);
        }
        catch (Exception ex)
        {
            App.Logger.Error("ui", "Could not open the output folder.", ex);
            ShowBanner("The output folder could not be opened.", error: true);
        }
    }

    private void RemoveFile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: QueuedFileItem item })
        {
            return;
        }

        RemoveFromQueue(item);
    }

    private void RemoveFromQueue(QueuedFileItem item)
    {
        App.Services.Queue.Remove(item.File.Path);
        _files.Remove(item);

        if (_selectedFileItem == item)
        {
            _selectedFileItem = _files.FirstOrDefault();
            FileList.SelectedItem = _selectedFileItem;
        }

        RefreshQueueChrome();
        ShowBanner($"Removed {item.FileName} from the queue. Disk file was not deleted.", error: false);
    }

    private void ClearQueue_Click(object sender, RoutedEventArgs e)
    {
        App.Services.Queue.Clear();
        _files.Clear();
        _selectedFileItem = null;
        _selectedAction = null;
        _radialItems.Clear();

        EmptyRadialPlaceholder.Visibility = Visibility.Visible;
        RadialMenuCanvas.Visibility = Visibility.Collapsed;
        NoActionPlaceholder.Visibility = Visibility.Visible;
        ActionConfigPanel.Visibility = Visibility.Collapsed;

        RefreshQueueChrome();
        ShowBanner("Queue cleared. No files on disk were deleted.", error: false);
    }

    private void Diagnostics_Click(object sender, RoutedEventArgs e) => OpenDiagnostics();

    private void OpenDiagnostics()
    {
        if (_diagnostics is { IsVisible: true })
        {
            _diagnostics.Activate();
            return;
        }

        _diagnostics = new DiagnosticsWindow();
        _diagnostics.Closed += (_, _) => _diagnostics = null;
        _diagnostics.Show();
    }

    private void CloseBanner_Click(object sender, RoutedEventArgs e)
    {
        Banner.Visibility = Visibility.Collapsed;
    }

    private void ShowBanner(string message, bool error, bool warning = false)
    {
        Banner.Visibility = Visibility.Visible;
        BannerText.Text = message;
        if (error)
        {
            Banner.Background = (Brush)FindResource("DangerSoftBrush");
            Banner.BorderBrush = (Brush)FindResource("DangerBrush");
            BannerText.Foreground = (Brush)FindResource("DangerBrush");
        }
        else if (warning)
        {
            Banner.Background = (Brush)FindResource("WarnSoftBrush");
            Banner.BorderBrush = (Brush)FindResource("WarnBrush");
            BannerText.Foreground = (Brush)FindResource("WarnBrush");
        }
        else
        {
            Banner.Background = (Brush)FindResource("OkSoftBrush");
            Banner.BorderBrush = (Brush)FindResource("OkBrush");
            BannerText.Foreground = (Brush)FindResource("OkBrush");
        }
    }

    private void RefreshQueueChrome()
    {
        var count = _files.Count;
        var supported = _files.Count(f => f.File.IsSupportedCategory);

        QueueCountText.Text = $"{count} file{(count == 1 ? "" : "s")}";
        if (NavQueueBadgeText is not null)
        {
            NavQueueBadgeText.Text = count.ToString();
        }

        if (count == 0)
        {
            EmptyState.Visibility = Visibility.Visible;
            FileList.Visibility = Visibility.Collapsed;
            CompactDropCue.Visibility = Visibility.Collapsed;
            QueueSummary.Text = "Drop files to begin.";
        }
        else
        {
            EmptyState.Visibility = Visibility.Collapsed;
            FileList.Visibility = Visibility.Visible;
            CompactDropCue.Visibility = Visibility.Visible;
            QueueSummary.Text = $"{count} queued · {supported} recognized";
        }

        var imageCount = _files.Count(f => f.File.Format is DetectedFormat.Jpeg or DetectedFormat.Png or DetectedFormat.Webp);
        if (ShelfBatchButton is not null)
        {
            if (imageCount >= 2)
            {
                ShelfBatchButton.Visibility = Visibility.Visible;
                ShelfBatchButtonText.Text = $"Batch Process ({imageCount} Images)";
            }
            else
            {
                ShelfBatchButton.Visibility = Visibility.Collapsed;
            }
        }
        if (ShelfBuildDocButton is not null)
        {
            if (imageCount >= 1)
            {
                ShelfBuildDocButton.Visibility = Visibility.Visible;
                ShelfBuildDocButtonText.Text = $"Build Document ({imageCount} Image{(imageCount == 1 ? "" : "s")})";
            }
            else
            {
                ShelfBuildDocButton.Visibility = Visibility.Collapsed;
            }
        }
    }

    // ==========================================
    // SIDEBAR & BROWSE WORKSPACE CONTROLS
    // ==========================================

    private void NavDocBuilder_Click(object sender, RoutedEventArgs e)
    {
        RecentResultsFlyout.Visibility = Visibility.Collapsed;
        SettingsFlyout.Visibility = Visibility.Collapsed;
        OpenDocumentBuilder();
    }

    private void ToggleSidebar_Click(object sender, RoutedEventArgs e)
    {
        _sidebarExpanded = !_sidebarExpanded;
        SidebarColDef.Width = new GridLength(_sidebarExpanded ? 180 : 64);
        var vis = _sidebarExpanded ? Visibility.Visible : Visibility.Collapsed;
        NavWorkspaceText.Visibility = vis;
        NavQueueText.Visibility = vis;
        NavDocBuilderText.Visibility = vis;
        NavRecentText.Visibility = vis;
        NavSettingsText.Visibility = vis;
    }

    private void NavWorkspace_Click(object sender, RoutedEventArgs e)
    {
        RecentResultsFlyout.Visibility = Visibility.Collapsed;
        SettingsFlyout.Visibility = Visibility.Collapsed;
        CloseBatchStudio();
        CloseDocumentBuilder();
        NavWorkspaceBtn.Tag = "Active";
        NavQueueBtn.Tag = null;
        NavDocBuilderBtn.Tag = null;
        NavRecentBtn.Tag = null;
        NavSettingsBtn.Tag = null;
    }

    private void NavQueue_Click(object sender, RoutedEventArgs e)
    {
        RecentResultsFlyout.Visibility = Visibility.Collapsed;
        SettingsFlyout.Visibility = Visibility.Collapsed;
        OpenBatchStudio();
    }

    private void NavRecent_Click(object sender, RoutedEventArgs e)
    {
        SettingsFlyout.Visibility = Visibility.Collapsed;
        RecentResultsFlyout.Visibility = RecentResultsFlyout.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    }

    private void NavSettings_Click(object sender, RoutedEventArgs e)
    {
        RecentResultsFlyout.Visibility = Visibility.Collapsed;
        SettingsFlyout.Visibility = SettingsFlyout.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    }

    private void CloseRecentFlyout_Click(object sender, RoutedEventArgs e)
    {
        RecentResultsFlyout.Visibility = Visibility.Collapsed;
    }

    private void CloseSettingsFlyout_Click(object sender, RoutedEventArgs e)
    {
        SettingsFlyout.Visibility = Visibility.Collapsed;
    }

    private void BrowseFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select files to optimize or convert",
            Multiselect = true,
            Filter = "Supported files (*.png;*.jpg;*.jpeg;*.webp;*.pdf)|*.png;*.jpg;*.jpeg;*.webp;*.pdf|All files (*.*)|*.*"
        };

        if (dialog.ShowDialog(this) == true && dialog.FileNames.Length > 0)
        {
            _ = IngestAsync(dialog.FileNames);
        }
    }

    // ==========================================
    // KEYBOARD NAVIGATION (Section 3.C & 6)
    // ==========================================

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Don't intercept hotkeys if user is actively typing in a TextBox
        if (Keyboard.FocusedElement is TextBox)
        {
            if (e.Key == Key.Escape)
            {
                Keyboard.ClearFocus();
                e.Handled = true;
            }
            return;
        }

        // Native Image Editor Hotkeys
        if (ImageEditorHost.Visibility == Visibility.Visible)
        {
            if (e.Key == Key.Escape)
            {
                CloseImageEditor();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.S && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                EditorSaveExport_Click(sender, e);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.R && (Keyboard.Modifiers & ModifierKeys.Control) == 0)
            {
                EditorRotateCw_Click(sender, e);
                e.Handled = true;
                return;
            }
            return;
        }

        // Native PDF Preview Hotkeys
        if (PdfPreviewHost.Visibility == Visibility.Visible)
        {
            if (e.Key == Key.Escape)
            {
                ClosePdfPreview();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Left || e.Key == Key.PageUp)
            {
                NavigatePrevPage();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Right || e.Key == Key.PageDown)
            {
                NavigateNextPage();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.OemPlus || e.Key == Key.Add)
            {
                ZoomInPreview();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.OemMinus || e.Key == Key.Subtract)
            {
                ZoomOutPreview();
                e.Handled = true;
                return;
            }
        }

        // Hotkeys 1-7 activate available actions
        int? index = e.Key switch
        {
            Key.D1 or Key.NumPad1 => 0,
            Key.D2 or Key.NumPad2 => 1,
            Key.D3 or Key.NumPad3 => 2,
            Key.D4 or Key.NumPad4 => 3,
            Key.D5 or Key.NumPad5 => 4,
            Key.D6 or Key.NumPad6 => 5,
            Key.D7 or Key.NumPad7 => 6,
            _ => null
        };

        if (index.HasValue && index.Value < _radialItems.Count)
        {
            var item = _radialItems[index.Value];
            if (item.IsAvailable)
            {
                SelectRadialAction(item);
                e.Handled = true;
                return;
            }
        }

        if (e.Key == Key.Delete && _selectedFileItem != null)
        {
            RemoveFromQueue(_selectedFileItem);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && RunButton.IsEnabled && !_busy)
        {
            RunAction_Click(sender, e);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            CancelAction_Click(sender, e);
            e.Handled = true;
        }
    }

    // ==========================================
    // NATIVE PDF PREVIEW CONTROLS & RENDERING
    // ==========================================

    public async Task OpenPdfPreviewAsync()
    {
        if (_selectedFileItem is null || !File.Exists(_selectedFileItem.File.Path))
        {
            return;
        }

        PdfPreviewHost.Visibility = Visibility.Visible;
        RadialMenuCanvas.Visibility = Visibility.Collapsed;
        if (CenterFileHeaderBar is not null)
        {
            CenterFileHeaderBar.Visibility = Visibility.Collapsed;
        }
        PdfPreviewErrorOverlay.Visibility = Visibility.Collapsed;

        var filePath = _selectedFileItem.File.Path;

        _previewRenderCts?.Cancel();
        _previewRenderCts = new CancellationTokenSource();
        var token = _previewRenderCts.Token;

        PdfPreviewLoadingOverlay.Visibility = Visibility.Visible;

        try
        {
            _previewDocInfo = await PdfPreviewRenderer.GetDocumentInfoAsync(filePath, token);
            if (token.IsCancellationRequested) return;

            if (_previewDocInfo.ErrorMessage != null)
            {
                PdfPreviewLoadingOverlay.Visibility = Visibility.Collapsed;
                PdfPreviewErrorTitle.Text = _previewDocInfo.IsEncrypted ? "Password-Protected PDF" : "Cannot Open PDF";
                PdfPreviewErrorMessage.Text = _previewDocInfo.ErrorMessage;
                PdfPreviewErrorOverlay.Visibility = Visibility.Visible;
                PdfPreviewDocPagesText.Text = "0";
                PdfPreviewDocDimsText.Text = "—";
                PdfPreviewDocSecurityText.Text = _previewDocInfo.IsEncrypted ? "Encrypted" : "Corrupted";
                PdfPreviewDocSecurityText.Foreground = (Brush)FindResource("DangerBrush");
                return;
            }

            PdfPreviewDocPagesText.Text = _previewDocInfo.TotalPages.ToString();
            PdfTotalPagesText.Text = $"of {_previewDocInfo.TotalPages}";
            PdfPreviewDocDimsText.Text = $"{Math.Round(_previewDocInfo.PageWidthPt)} × {Math.Round(_previewDocInfo.PageHeightPt)} pt";
            PdfPreviewDocSecurityText.Text = _previewDocInfo.IsEncrypted ? "Password Protected" : "None (Standard)";
            PdfPreviewDocSecurityText.Foreground = (Brush)FindResource("OkBrush");

            if (_previewCurrentPage < 1 || _previewCurrentPage > _previewDocInfo.TotalPages)
            {
                _previewCurrentPage = 1;
            }

            await RenderCurrentPreviewPageAsync();
        }
        catch (OperationCanceledException)
        {
            // Request superseded
        }
        catch (Exception ex)
        {
            PdfPreviewLoadingOverlay.Visibility = Visibility.Collapsed;
            PdfPreviewErrorTitle.Text = "Preview Error";
            PdfPreviewErrorMessage.Text = ex.Message;
            PdfPreviewErrorOverlay.Visibility = Visibility.Visible;
        }
    }

    private async Task RenderCurrentPreviewPageAsync()
    {
        if (_selectedFileItem is null || _previewDocInfo is null || _previewDocInfo.TotalPages <= 0)
        {
            return;
        }

        var filePath = _selectedFileItem.File.Path;
        _previewRenderCts?.Cancel();
        _previewRenderCts = new CancellationTokenSource();
        var token = _previewRenderCts.Token;

        PdfPreviewLoadingOverlay.Visibility = Visibility.Visible;

        try
        {
            double viewportW = Math.Max(120, PdfPreviewScrollViewer.ActualWidth - 64);
            double viewportH = Math.Max(120, PdfPreviewScrollViewer.ActualHeight - 64);
            double ptW = _previewDocInfo.PageWidthPt > 0 ? _previewDocInfo.PageWidthPt : 612.0;
            double ptH = _previewDocInfo.PageHeightPt > 0 ? _previewDocInfo.PageHeightPt : 792.0;

            double displayW;
            double displayH;

            switch (_previewFitMode)
            {
                case PreviewFitMode.FitWidth:
                    displayW = viewportW;
                    displayH = displayW * (ptH / ptW);
                    break;

                case PreviewFitMode.FitPage:
                    double scaleW = viewportW / ptW;
                    double scaleH = viewportH / ptH;
                    double fitScale = Math.Min(scaleW, scaleH);
                    displayW = ptW * fitScale;
                    displayH = ptH * fitScale;
                    break;

                case PreviewFitMode.None:
                default:
                    displayW = ptW * (96.0 / 72.0) * _previewZoomFactor;
                    displayH = ptH * (96.0 / 72.0) * _previewZoomFactor;
                    break;
            }

            double renderDpi = Math.Clamp((displayW / ptW) * 72.0, 50.0, 300.0);
            int dpiBucket = (int)Math.Round(renderDpi);
            int pageIndex = _previewCurrentPage - 1;

            PdfPreviewPageData? pageData;
            if (!_previewCache.TryGet(filePath, pageIndex, dpiBucket, out pageData) || pageData is null)
            {
                pageData = await Task.Run(() => PdfPreviewRenderer.RenderPageAsync(filePath, pageIndex, renderDpi, token), token);
                _previewCache.Set(filePath, pageIndex, dpiBucket, pageData);
            }

            if (token.IsCancellationRequested) return;

            var bitmap = BitmapSource.Create(
                pageData.PixelWidth,
                pageData.PixelHeight,
                96, 96,
                PixelFormats.Bgra32,
                null,
                pageData.BgraBytes,
                pageData.Stride);
            bitmap.Freeze();

            PdfPreviewImage.Source = bitmap;
            PdfPageContainer.Width = displayW;
            PdfPageContainer.Height = displayH;
            PdfPreviewImage.Width = displayW;
            PdfPreviewImage.Height = displayH;

            PdfCurrentPageBox.Text = _previewCurrentPage.ToString();
            PdfPrevPageBtn.IsEnabled = _previewCurrentPage > 1;
            PdfNextPageBtn.IsEnabled = _previewCurrentPage < _previewDocInfo.TotalPages;

            double zoomPercent = (displayW / (ptW * (96.0 / 72.0))) * 100.0;
            PdfZoomLevelText.Text = $"{Math.Round(zoomPercent)}%";
            PdfPreviewDocZoomText.Text = PdfZoomLevelText.Text;

            PdfPreviewLoadingOverlay.Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException)
        {
            // Ignored when superseded
        }
        catch (Exception ex)
        {
            PdfPreviewLoadingOverlay.Visibility = Visibility.Collapsed;
            PdfPreviewErrorTitle.Text = "Rendering Error";
            PdfPreviewErrorMessage.Text = ex.Message;
            PdfPreviewErrorOverlay.Visibility = Visibility.Visible;
        }
    }

    public void ClosePdfPreview()
    {
        _previewRenderCts?.Cancel();
        PdfPreviewHost.Visibility = Visibility.Collapsed;
        PdfPreviewErrorOverlay.Visibility = Visibility.Collapsed;
        PdfPreviewLoadingOverlay.Visibility = Visibility.Collapsed;
        RadialMenuCanvas.Visibility = Visibility.Visible;
        if (CenterFileHeaderBar is not null && _selectedFileItem is not null)
        {
            CenterFileHeaderBar.Visibility = Visibility.Visible;
        }
    }

    private void NavigatePrevPage()
    {
        if (_previewDocInfo != null && _previewCurrentPage > 1)
        {
            _previewCurrentPage--;
            _ = RenderCurrentPreviewPageAsync();
        }
    }

    private void NavigateNextPage()
    {
        if (_previewDocInfo != null && _previewCurrentPage < _previewDocInfo.TotalPages)
        {
            _previewCurrentPage++;
            _ = RenderCurrentPreviewPageAsync();
        }
    }

    private void ZoomInPreview()
    {
        if (_previewDocInfo == null) return;
        double ptW = _previewDocInfo.PageWidthPt > 0 ? _previewDocInfo.PageWidthPt : 612.0;
        double currentDisplayW = PdfPageContainer.Width > 0 ? PdfPageContainer.Width : ptW * (96.0 / 72.0);
        double currentFactor = currentDisplayW / (ptW * (96.0 / 72.0));
        _previewFitMode = PreviewFitMode.None;
        _previewZoomFactor = Math.Min(5.0, currentFactor * 1.25);
        _ = RenderCurrentPreviewPageAsync();
    }

    private void ZoomOutPreview()
    {
        if (_previewDocInfo == null) return;
        double ptW = _previewDocInfo.PageWidthPt > 0 ? _previewDocInfo.PageWidthPt : 612.0;
        double currentDisplayW = PdfPageContainer.Width > 0 ? PdfPageContainer.Width : ptW * (96.0 / 72.0);
        double currentFactor = currentDisplayW / (ptW * (96.0 / 72.0));
        _previewFitMode = PreviewFitMode.None;
        _previewZoomFactor = Math.Max(0.25, currentFactor / 1.25);
        _ = RenderCurrentPreviewPageAsync();
    }

    private void ClosePreview_Click(object sender, RoutedEventArgs e)
    {
        ClosePdfPreview();
    }

    private void PdfOpenPreview_Click(object sender, RoutedEventArgs e)
    {
        _ = OpenPdfPreviewAsync();
    }

    private void PdfPrevPage_Click(object sender, RoutedEventArgs e)
    {
        NavigatePrevPage();
    }

    private void PdfNextPage_Click(object sender, RoutedEventArgs e)
    {
        NavigateNextPage();
    }

    private void PdfCurrentPageBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ApplyPreviewPageInput();
            Keyboard.ClearFocus();
            e.Handled = true;
        }
    }

    private void PdfCurrentPageBox_LostFocus(object sender, RoutedEventArgs e)
    {
        ApplyPreviewPageInput();
    }

    private void ApplyPreviewPageInput()
    {
        if (_previewDocInfo == null || _previewDocInfo.TotalPages <= 0) return;
        if (int.TryParse(PdfCurrentPageBox.Text.Trim(), out int page))
        {
            int clamped = Math.Clamp(page, 1, _previewDocInfo.TotalPages);
            if (clamped != _previewCurrentPage)
            {
                _previewCurrentPage = clamped;
                _ = RenderCurrentPreviewPageAsync();
            }
            else
            {
                PdfCurrentPageBox.Text = _previewCurrentPage.ToString();
            }
        }
        else
        {
            PdfCurrentPageBox.Text = _previewCurrentPage.ToString();
        }
    }

    private void PdfZoomIn_Click(object sender, RoutedEventArgs e)
    {
        ZoomInPreview();
    }

    private void PdfZoomOut_Click(object sender, RoutedEventArgs e)
    {
        ZoomOutPreview();
    }

    private void PdfFitPage_Click(object sender, RoutedEventArgs e)
    {
        _previewFitMode = PreviewFitMode.FitPage;
        _ = RenderCurrentPreviewPageAsync();
    }

    private void PdfFitWidth_Click(object sender, RoutedEventArgs e)
    {
        _previewFitMode = PreviewFitMode.FitWidth;
        _ = RenderCurrentPreviewPageAsync();
    }

    private void PdfPreviewScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (PdfPreviewHost.Visibility == Visibility.Visible &&
            _previewFitMode != PreviewFitMode.None &&
            _previewDocInfo != null &&
            !_previewDocInfo.IsEncrypted &&
            _previewDocInfo.TotalPages > 0)
        {
            _ = RenderCurrentPreviewPageAsync();
        }
    }

    // ==========================================
    // INTERACTIVE IMAGE EDITOR
    // ==========================================

    public void OpenImageEditor(ClassifiedFile? targetFile = null)
    {
        if (DocumentBuilderHost.Visibility == Visibility.Visible)
        {
            CloseDocumentBuilder();
        }
        var file = targetFile ?? _selectedFileItem?.File;
        if (file is null || !File.Exists(file.Path))
        {
            return;
        }

        if (file.Format is not (DetectedFormat.Png or DetectedFormat.Jpeg or DetectedFormat.Webp))
        {
            ShowBanner("Only JPEG, PNG, and WebP files can be edited.", error: true);
            return;
        }

        try
        {
            _imageEditSession?.Dispose();
            _imageEditSession = new ImageEditSession(file.Path);

            ImageEditorHost.Visibility = Visibility.Visible;
            RadialMenuCanvas.Visibility = Visibility.Collapsed;
            if (CenterFileHeaderBar is not null)
            {
                CenterFileHeaderBar.Visibility = Visibility.Collapsed;
            }

            if (EditorHeaderFileNameText is not null)
            {
                EditorHeaderFileNameText.Text = file.FileName;
            }
            if (EditorHeaderFormatBadge is not null)
            {
                EditorHeaderFormatBadge.Text = file.Extension.TrimStart('.').ToUpperInvariant();
            }

            _cropPixelRect = new SKRectI(0, 0, _imageEditSession.CurrentWidth, _imageEditSession.CurrentHeight);
            _targetCropAspectRatio = null;
            UpdateEditorAspectButtons("free");

            _updatingEditorResize = true;
            EditorResizeWidthBox.Text = _imageEditSession.CurrentWidth.ToString();
            EditorResizeHeightBox.Text = _imageEditSession.CurrentHeight.ToString();
            _updatingEditorResize = false;

            EditorResizeValidationText.Visibility = Visibility.Collapsed;
            EditorProcessingOverlay.Visibility = Visibility.Collapsed;

            Dispatcher.InvokeAsync(UpdateEditorPreview, System.Windows.Threading.DispatcherPriority.Loaded);
        }
        catch (Exception ex)
        {
            ShowBanner($"Failed to open image in editor: {ex.Message}", error: true);
            CloseImageEditor();
        }
    }

    public void CloseImageEditor()
    {
        _imageEditSession?.Dispose();
        _imageEditSession = null;

        ImageEditorHost.Visibility = Visibility.Collapsed;
        RadialMenuCanvas.Visibility = Visibility.Visible;
        if (CenterFileHeaderBar is not null && _selectedFileItem is not null)
        {
            CenterFileHeaderBar.Visibility = Visibility.Visible;
        }
    }

    private void CloseImageEditor_Click(object sender, RoutedEventArgs e) => CloseImageEditor();

    private void EditorResetAll_Click(object sender, RoutedEventArgs e)
    {
        if (_imageEditSession is null) return;
        _imageEditSession.Reset();
        _cropPixelRect = new SKRectI(0, 0, _imageEditSession.CurrentWidth, _imageEditSession.CurrentHeight);
        _targetCropAspectRatio = null;
        UpdateEditorAspectButtons("free");
        EditorResizeValidationText.Visibility = Visibility.Collapsed;
        UpdateEditorPreview();
    }

    private async void EditorSaveExport_Click(object sender, RoutedEventArgs e)
    {
        if (_imageEditSession is null || _selectedFileItem is null) return;

        EditorProcessingOverlay.Visibility = Visibility.Visible;
        EditorProcessingText.Text = "Saving to output directory…";

        try
        {
            var result = await Task.Run(() => _imageEditSession.Save(App.Services.Outputs, App.Services.Outputs.RootDirectory));
            EditorProcessingOverlay.Visibility = Visibility.Collapsed;

            if (result.Succeeded)
            {
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
                ShowBanner(result.Status, error: false);
                CloseImageEditor();
            }
            else
            {
                ShowBanner(result.Status, error: true);
            }
        }
        catch (Exception ex)
        {
            EditorProcessingOverlay.Visibility = Visibility.Collapsed;
            ShowBanner($"Failed to save image: {ex.Message}", error: true);
        }
    }

    private void UpdateEditorPreview()
    {
        if (_imageEditSession is null || EditorViewportCanvas is null) return;

        var w = _imageEditSession.CurrentWidth;
        var h = _imageEditSession.CurrentHeight;
        EditorCurrentDimensionsText.Text = $"{w} × {h} px";

        if (!_updatingEditorResize)
        {
            _updatingEditorResize = true;
            EditorResizeWidthBox.Text = w.ToString();
            EditorResizeHeightBox.Text = h.ToString();
            _updatingEditorResize = false;
        }

        var sk = _imageEditSession.WorkingBitmap;
        var bmp = BitmapSource.Create(
            sk.Width,
            sk.Height,
            96, 96,
            PixelFormats.Bgra32,
            null,
            sk.GetPixels(),
            sk.ByteCount,
            sk.RowBytes);
        bmp.Freeze();
        EditorPreviewImage.Source = bmp;

        _editorViewport = ImageCoordinateMapper.ComputeViewport(EditorViewportCanvas.ActualWidth, EditorViewportCanvas.ActualHeight, w, h);
        Canvas.SetLeft(EditorPreviewImage, _editorViewport.OffsetX);
        Canvas.SetTop(EditorPreviewImage, _editorViewport.OffsetY);
        EditorPreviewImage.Width = _editorViewport.DisplayedWidth;
        EditorPreviewImage.Height = _editorViewport.DisplayedHeight;

        UpdateCropOverlay();
    }

    private void UpdateCropOverlay()
    {
        if (_imageEditSession is null || EditorViewportCanvas is null) return;

        var maxW = _imageEditSession.CurrentWidth;
        var maxH = _imageEditSession.CurrentHeight;

        _cropPixelRect = new SKRectI(
            Math.Clamp(_cropPixelRect.Left, 0, maxW),
            Math.Clamp(_cropPixelRect.Top, 0, maxH),
            Math.Clamp(_cropPixelRect.Right, 0, maxW),
            Math.Clamp(_cropPixelRect.Bottom, 0, maxH));

        if (_cropPixelRect.Width <= 0 || _cropPixelRect.Height <= 0)
        {
            _cropPixelRect = new SKRectI(0, 0, maxW, maxH);
        }

        var (cx, cy, cw, ch) = ImageCoordinateMapper.ImagePixelToContainer(_cropPixelRect, _editorViewport);

        Canvas.SetLeft(CropSelectionBox, cx);
        Canvas.SetTop(CropSelectionBox, cy);
        CropSelectionBox.Width = Math.Max(0, cw);
        CropSelectionBox.Height = Math.Max(0, ch);

        Canvas.SetLeft(CropMaskTop, 0);
        Canvas.SetTop(CropMaskTop, 0);
        CropMaskTop.Width = Math.Max(0, EditorViewportCanvas.ActualWidth);
        CropMaskTop.Height = Math.Max(0, cy);

        Canvas.SetLeft(CropMaskBottom, 0);
        Canvas.SetTop(CropMaskBottom, cy + ch);
        CropMaskBottom.Width = Math.Max(0, EditorViewportCanvas.ActualWidth);
        CropMaskBottom.Height = Math.Max(0, EditorViewportCanvas.ActualHeight - (cy + ch));

        Canvas.SetLeft(CropMaskLeft, 0);
        Canvas.SetTop(CropMaskLeft, cy);
        CropMaskLeft.Width = Math.Max(0, cx);
        CropMaskLeft.Height = Math.Max(0, ch);

        Canvas.SetLeft(CropMaskRight, cx + cw);
        Canvas.SetTop(CropMaskRight, cy);
        CropMaskRight.Width = Math.Max(0, EditorViewportCanvas.ActualWidth - (cx + cw));
        CropMaskRight.Height = Math.Max(0, ch);

        PositionHandle(HandleTopLeft, cx - 5, cy - 5);
        PositionHandle(HandleTop, cx + (cw / 2) - 5, cy - 5);
        PositionHandle(HandleTopRight, cx + cw - 5, cy - 5);
        PositionHandle(HandleRight, cx + cw - 5, cy + (ch / 2) - 5);
        PositionHandle(HandleBottomRight, cx + cw - 5, cy + ch - 5);
        PositionHandle(HandleBottom, cx + (cw / 2) - 5, cy + ch - 5);
        PositionHandle(HandleBottomLeft, cx - 5, cy + ch - 5);
        PositionHandle(HandleLeft, cx - 5, cy + (ch / 2) - 5);

        CropBadgeText.Text = $"{_cropPixelRect.Width} × {_cropPixelRect.Height} px";
        EditorCropDimensionsText.Text = $"{_cropPixelRect.Width} × {_cropPixelRect.Height} px";

        var badgeX = Math.Clamp(cx + (cw / 2) - 55, 8, Math.Max(8, EditorViewportCanvas.ActualWidth - 120));
        var badgeY = (cy + ch + 8 + 26 <= EditorViewportCanvas.ActualHeight) ? cy + ch + 8 : Math.Max(8, cy - 30);
        Canvas.SetLeft(CropDimensionsBadge, badgeX);
        Canvas.SetTop(CropDimensionsBadge, badgeY);
    }

    private static void PositionHandle(FrameworkElement handle, double left, double top)
    {
        Canvas.SetLeft(handle, left);
        Canvas.SetTop(handle, top);
    }

    private CropHandle HitTestCropHandle(Point pt)
    {
        if (_imageEditSession is null) return CropHandle.None;
        var (cx, cy, cw, ch) = ImageCoordinateMapper.ImagePixelToContainer(_cropPixelRect, _editorViewport);

        bool Near(double hx, double hy) => Math.Abs(pt.X - hx) <= 10 && Math.Abs(pt.Y - hy) <= 10;

        if (Near(cx, cy)) return CropHandle.TopLeft;
        if (Near(cx + cw, cy)) return CropHandle.TopRight;
        if (Near(cx + cw, cy + ch)) return CropHandle.BottomRight;
        if (Near(cx, cy + ch)) return CropHandle.BottomLeft;
        if (Near(cx + cw / 2, cy)) return CropHandle.Top;
        if (Near(cx + cw, cy + ch / 2)) return CropHandle.Right;
        if (Near(cx + cw / 2, cy + ch)) return CropHandle.Bottom;
        if (Near(cx, cy + ch / 2)) return CropHandle.Left;

        if (pt.X >= cx && pt.X <= cx + cw && pt.Y >= cy && pt.Y <= cy + ch)
        {
            return CropHandle.Move;
        }

        return CropHandle.None;
    }

    private void EditorViewportCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ImageEditorHost.Visibility == Visibility.Visible && _imageEditSession is not null)
        {
            UpdateEditorPreview();
        }
    }

    private void EditorViewportCanvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_imageEditSession is null || e.LeftButton != MouseButtonState.Pressed) return;

        var pt = e.GetPosition(EditorViewportCanvas);
        var handle = HitTestCropHandle(pt);

        _dragStartPoint = pt;
        _dragStartCropRect = _cropPixelRect;

        if (handle != CropHandle.None)
        {
            _activeDragHandle = handle;
            _isDrawingNewCrop = false;
        }
        else
        {
            // Click outside existing box: start drawing a new crop box
            _isDrawingNewCrop = true;
            _activeDragHandle = CropHandle.BottomRight;
            var (px, py) = ImageCoordinateMapper.ContainerToImagePixel(pt.X, pt.Y, _editorViewport, _imageEditSession.CurrentWidth, _imageEditSession.CurrentHeight);
            _cropPixelRect = new SKRectI(px, py, px + 1, py + 1);
            UpdateCropOverlay();
        }

        EditorViewportCanvas.CaptureMouse();
    }

    private void EditorViewportCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (_imageEditSession is null) return;
        var pt = e.GetPosition(EditorViewportCanvas);

        if (_activeDragHandle == CropHandle.None)
        {
            // Hover cursor update
            var handle = HitTestCropHandle(pt);
            EditorViewportCanvas.Cursor = handle switch
            {
                CropHandle.TopLeft or CropHandle.BottomRight => Cursors.SizeNWSE,
                CropHandle.TopRight or CropHandle.BottomLeft => Cursors.SizeNESW,
                CropHandle.Top or CropHandle.Bottom => Cursors.SizeNS,
                CropHandle.Left or CropHandle.Right => Cursors.SizeWE,
                CropHandle.Move => Cursors.SizeAll,
                _ => _editorViewport.ContainsContainerPoint(pt.X, pt.Y) ? Cursors.Cross : Cursors.Arrow
            };
            return;
        }

        var (curPx, curPy) = ImageCoordinateMapper.ContainerToImagePixel(pt.X, pt.Y, _editorViewport, _imageEditSession.CurrentWidth, _imageEditSession.CurrentHeight);

        if (_activeDragHandle == CropHandle.Move)
        {
            int dx = (int)Math.Round((pt.X - _dragStartPoint.X) / _editorViewport.Scale);
            int dy = (int)Math.Round((pt.Y - _dragStartPoint.Y) / _editorViewport.Scale);
            int newLeft = Math.Clamp(_dragStartCropRect.Left + dx, 0, _imageEditSession.CurrentWidth - _dragStartCropRect.Width);
            int newTop = Math.Clamp(_dragStartCropRect.Top + dy, 0, _imageEditSession.CurrentHeight - _dragStartCropRect.Height);
            _cropPixelRect = new SKRectI(newLeft, newTop, newLeft + _dragStartCropRect.Width, newTop + _dragStartCropRect.Height);
        }
        else if (_isDrawingNewCrop)
        {
            var (startPx, startPy) = ImageCoordinateMapper.ContainerToImagePixel(_dragStartPoint.X, _dragStartPoint.Y, _editorViewport, _imageEditSession.CurrentWidth, _imageEditSession.CurrentHeight);
            var rect = ImageCoordinateMapper.NormalizeRect(startPx, startPy, curPx, curPy, _imageEditSession.CurrentWidth, _imageEditSession.CurrentHeight);
            if (_targetCropAspectRatio.HasValue)
            {
                rect = ImageCoordinateMapper.ConstrainToAspectRatio(rect, _targetCropAspectRatio, _imageEditSession.CurrentWidth, _imageEditSession.CurrentHeight, CropHandle.None);
            }
            _cropPixelRect = rect;
        }
        else
        {
            // Dragging an existing handle
            int left = _dragStartCropRect.Left;
            int top = _dragStartCropRect.Top;
            int right = _dragStartCropRect.Right;
            int bottom = _dragStartCropRect.Bottom;

            switch (_activeDragHandle)
            {
                case CropHandle.TopLeft:
                    left = curPx;
                    top = curPy;
                    break;
                case CropHandle.Top:
                    top = curPy;
                    break;
                case CropHandle.TopRight:
                    right = curPx;
                    top = curPy;
                    break;
                case CropHandle.Right:
                    right = curPx;
                    break;
                case CropHandle.BottomRight:
                    right = curPx;
                    bottom = curPy;
                    break;
                case CropHandle.Bottom:
                    bottom = curPy;
                    break;
                case CropHandle.BottomLeft:
                    left = curPx;
                    bottom = curPy;
                    break;
                case CropHandle.Left:
                    left = curPx;
                    break;
            }

            var rect = ImageCoordinateMapper.NormalizeRect(left, top, right, bottom, _imageEditSession.CurrentWidth, _imageEditSession.CurrentHeight);
            if (rect.Width < 10) rect = new SKRectI(rect.Left, rect.Top, Math.Min(_imageEditSession.CurrentWidth, rect.Left + 10), rect.Bottom);
            if (rect.Height < 10) rect = new SKRectI(rect.Left, rect.Top, rect.Right, Math.Min(_imageEditSession.CurrentHeight, rect.Top + 10));

            if (_targetCropAspectRatio.HasValue)
            {
                rect = ImageCoordinateMapper.ConstrainToAspectRatio(rect, _targetCropAspectRatio, _imageEditSession.CurrentWidth, _imageEditSession.CurrentHeight, _activeDragHandle);
            }

            _cropPixelRect = rect;
        }

        UpdateCropOverlay();
    }

    private void EditorViewportCanvas_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_activeDragHandle != CropHandle.None)
        {
            EditorViewportCanvas.ReleaseMouseCapture();
            _activeDragHandle = CropHandle.None;
            _isDrawingNewCrop = false;

            if (_cropPixelRect.Width < 10 || _cropPixelRect.Height < 10)
            {
                _cropPixelRect = new SKRectI(0, 0, _imageEditSession?.CurrentWidth ?? 100, _imageEditSession?.CurrentHeight ?? 100);
            }
            UpdateCropOverlay();
        }
    }

    private void EditorViewportCanvas_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_activeDragHandle == CropHandle.None)
        {
            EditorViewportCanvas.Cursor = Cursors.Arrow;
        }
    }

    private void EditorAspect_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string tag || _imageEditSession is null) return;

        UpdateEditorAspectButtons(tag);

        _targetCropAspectRatio = tag switch
        {
            "original" => _imageEditSession.CurrentWidth / (double)_imageEditSession.CurrentHeight,
            "1:1" => 1.0,
            "4:3" => 4.0 / 3.0,
            "16:9" => 16.0 / 9.0,
            _ => null
        };

        if (_targetCropAspectRatio.HasValue)
        {
            _cropPixelRect = ImageCoordinateMapper.ConstrainToAspectRatio(
                _cropPixelRect,
                _targetCropAspectRatio,
                _imageEditSession.CurrentWidth,
                _imageEditSession.CurrentHeight);
            UpdateCropOverlay();
        }
    }

    private void UpdateEditorAspectButtons(string activeTag)
    {
        var primary = (Style)FindResource("PrimaryButton");
        var secondary = (Style)FindResource("SecondaryButton");

        EditorAspectFreeBtn.Style = activeTag == "free" ? primary : secondary;
        EditorAspectOriginalBtn.Style = activeTag == "original" ? primary : secondary;
        EditorAspect1x1Btn.Style = activeTag == "1:1" ? primary : secondary;
        EditorAspect4x3Btn.Style = activeTag == "4:3" ? primary : secondary;
        EditorAspect16x9Btn.Style = activeTag == "16:9" ? primary : secondary;
    }

    private void EditorApplyCrop_Click(object sender, RoutedEventArgs e)
    {
        if (_imageEditSession is null) return;
        if (_cropPixelRect.Width <= 0 || _cropPixelRect.Height <= 0) return;

        try
        {
            _imageEditSession.Crop(_cropPixelRect);
            _cropPixelRect = new SKRectI(0, 0, _imageEditSession.CurrentWidth, _imageEditSession.CurrentHeight);
            UpdateEditorPreview();
        }
        catch (Exception ex)
        {
            ShowBanner($"Crop failed: {ex.Message}", error: true);
        }
    }

    private void EditorResetCropSelection_Click(object sender, RoutedEventArgs e)
    {
        if (_imageEditSession is null) return;
        _cropPixelRect = new SKRectI(0, 0, _imageEditSession.CurrentWidth, _imageEditSession.CurrentHeight);
        UpdateCropOverlay();
    }

    private void EditorRotateCw_Click(object sender, RoutedEventArgs e)
    {
        if (_imageEditSession is null) return;
        _imageEditSession.Rotate90Clockwise();
        _cropPixelRect = new SKRectI(0, 0, _imageEditSession.CurrentWidth, _imageEditSession.CurrentHeight);
        UpdateEditorPreview();
    }

    private void EditorRotateCcw_Click(object sender, RoutedEventArgs e)
    {
        if (_imageEditSession is null) return;
        _imageEditSession.Rotate90CounterClockwise();
        _cropPixelRect = new SKRectI(0, 0, _imageEditSession.CurrentWidth, _imageEditSession.CurrentHeight);
        UpdateEditorPreview();
    }

    private void EditorFlipH_Click(object sender, RoutedEventArgs e)
    {
        if (_imageEditSession is null) return;
        _imageEditSession.FlipHorizontal();
        UpdateEditorPreview();
    }

    private void EditorFlipV_Click(object sender, RoutedEventArgs e)
    {
        if (_imageEditSession is null) return;
        _imageEditSession.FlipVertical();
        UpdateEditorPreview();
    }

    private void EditorResizeLockAspectCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (EditorResizeLockAspectCheck.IsChecked == true && _imageEditSession is not null)
        {
            RecalculateEditorProportionalHeight();
        }
    }

    private void EditorResizeWidthBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updatingEditorResize || EditorResizeLockAspectCheck?.IsChecked != true) return;
        RecalculateEditorProportionalHeight();
    }

    private void EditorResizeHeightBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updatingEditorResize || EditorResizeLockAspectCheck?.IsChecked != true) return;
        RecalculateEditorProportionalWidth();
    }

    private void RecalculateEditorProportionalHeight()
    {
        if (_imageEditSession is null) return;
        if (int.TryParse(EditorResizeWidthBox.Text, out int w) && w > 0 &&
            _imageEditSession.CurrentWidth > 0 && _imageEditSession.CurrentHeight > 0)
        {
            int h = Math.Max(1, (int)Math.Round(w * (_imageEditSession.CurrentHeight / (double)_imageEditSession.CurrentWidth)));
            _updatingEditorResize = true;
            EditorResizeHeightBox.Text = h.ToString();
            _updatingEditorResize = false;
        }
    }

    private void RecalculateEditorProportionalWidth()
    {
        if (_imageEditSession is null) return;
        if (int.TryParse(EditorResizeHeightBox.Text, out int h) && h > 0 &&
            _imageEditSession.CurrentWidth > 0 && _imageEditSession.CurrentHeight > 0)
        {
            int w = Math.Max(1, (int)Math.Round(h * (_imageEditSession.CurrentWidth / (double)_imageEditSession.CurrentHeight)));
            _updatingEditorResize = true;
            EditorResizeWidthBox.Text = w.ToString();
            _updatingEditorResize = false;
        }
    }

    private void EditorPreset25_Click(object sender, RoutedEventArgs e) => ApplyEditorScalePreset(0.25);
    private void EditorPreset50_Click(object sender, RoutedEventArgs e) => ApplyEditorScalePreset(0.50);
    private void EditorPreset75_Click(object sender, RoutedEventArgs e) => ApplyEditorScalePreset(0.75);

    private void ApplyEditorScalePreset(double scale)
    {
        if (_imageEditSession is null) return;
        int w = Math.Max(1, (int)Math.Round(_imageEditSession.CurrentWidth * scale));
        int h = Math.Max(1, (int)Math.Round(_imageEditSession.CurrentHeight * scale));
        _updatingEditorResize = true;
        EditorResizeWidthBox.Text = w.ToString();
        EditorResizeHeightBox.Text = h.ToString();
        _updatingEditorResize = false;
    }

    private void EditorPreset720_Click(object sender, RoutedEventArgs e) => ApplyEditorLongestEdgePreset(720);
    private void EditorPreset1080_Click(object sender, RoutedEventArgs e) => ApplyEditorLongestEdgePreset(1080);

    private void ApplyEditorLongestEdgePreset(int targetLength)
    {
        if (_imageEditSession is null) return;
        int w, h;
        if (_imageEditSession.CurrentWidth >= _imageEditSession.CurrentHeight)
        {
            w = targetLength;
            h = Math.Max(1, (int)Math.Round(targetLength * (_imageEditSession.CurrentHeight / (double)_imageEditSession.CurrentWidth)));
        }
        else
        {
            h = targetLength;
            w = Math.Max(1, (int)Math.Round(targetLength * (_imageEditSession.CurrentWidth / (double)_imageEditSession.CurrentHeight)));
        }
        _updatingEditorResize = true;
        EditorResizeWidthBox.Text = w.ToString();
        EditorResizeHeightBox.Text = h.ToString();
        _updatingEditorResize = false;
    }

    private void EditorApplyResize_Click(object sender, RoutedEventArgs e)
    {
        if (_imageEditSession is null) return;

        if (!int.TryParse(EditorResizeWidthBox.Text, out int w) || w <= 0 ||
            !int.TryParse(EditorResizeHeightBox.Text, out int h) || h <= 0)
        {
            EditorResizeValidationText.Text = "Width and height must be positive numbers.";
            EditorResizeValidationText.Visibility = Visibility.Visible;
            return;
        }

        if (ImageSafetyLimits.Exceeds(w, h))
        {
            EditorResizeValidationText.Text = $"Dimensions exceed safety limits (max {ImageSafetyLimits.MaxDimension}px per side or {ImageSafetyLimits.MaxPixelCount:N0} pixels).";
            EditorResizeValidationText.Visibility = Visibility.Visible;
            return;
        }

        EditorResizeValidationText.Visibility = Visibility.Collapsed;

        try
        {
            _imageEditSession.Resize(w, h);
            _cropPixelRect = new SKRectI(0, 0, _imageEditSession.CurrentWidth, _imageEditSession.CurrentHeight);
            UpdateEditorPreview();
        }
        catch (Exception ex)
        {
            EditorResizeValidationText.Text = ex.Message;
            EditorResizeValidationText.Visibility = Visibility.Visible;
        }
    }

    // ==========================================
    // BATCH IMAGE STUDIO CONTROLS & ORCHESTRATION
    // ==========================================

    public void OpenBatchStudio()
    {
        if (ImageEditorHost.Visibility == Visibility.Visible)
        {
            CloseImageEditor();
        }
        if (DocumentBuilderHost.Visibility == Visibility.Visible)
        {
            CloseDocumentBuilder();
        }
        ClosePdfPreview();

        BatchStudioHost.Visibility = Visibility.Visible;
        NavQueueBtn.Tag = "Active";
        NavWorkspaceBtn.Tag = null;

        // Auto-populate from workspace files if batch queue is empty
        if (_batchFiles.Count == 0 && _files.Count > 0)
        {
            foreach (var item in _files)
            {
                if (item.File.Format is DetectedFormat.Jpeg or DetectedFormat.Png or DetectedFormat.Webp)
                {
                    if (!_batchFiles.Any(b => string.Equals(b.File.Path, item.File.Path, StringComparison.OrdinalIgnoreCase)))
                    {
                        _batchFiles.Add(new BatchQueueItemViewModel(item.File));
                    }
                }
            }
        }

        UpdateBatchQueueChrome();
    }

    public void CloseBatchStudio()
    {
        if (_isBatchRunning)
        {
            ShowBanner("A batch process is currently running. Cancel it first before closing.", error: false, warning: true);
            return;
        }

        BatchStudioHost.Visibility = Visibility.Collapsed;
        NavQueueBtn.Tag = null;
        NavWorkspaceBtn.Tag = "Active";
    }

    private void OpenBatchStudio_Click(object sender, RoutedEventArgs e) => OpenBatchStudio();

    private void CloseBatchStudio_Click(object sender, RoutedEventArgs e) => CloseBatchStudio();

    private void UpdateBatchQueueChrome()
    {
        int count = _batchFiles.Count;
        BatchStudioQueueCountText.Text = $"{count} image{(count == 1 ? "" : "s")}";
        BatchEmptyQueuePlaceholder.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        BatchFileList.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        BatchStartButton.IsEnabled = count > 0 && !_isBatchRunning;
    }

    private void BatchAddImages_Click(object sender, RoutedEventArgs e)
    {
        if (_isBatchRunning)
        {
            ShowBanner("Cannot add files while batch processing is active.", error: true);
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Multiselect = true,
            Title = "Select Images for Batch Processing",
            Filter = "Image Files (*.jpg;*.jpeg;*.png;*.webp)|*.jpg;*.jpeg;*.png;*.webp|All Files (*.*)|*.*"
        };

        if (dialog.ShowDialog(this) == true && dialog.FileNames.Length > 0)
        {
            int addedCount = 0;
            int duplicateCount = 0;
            int rejectedCount = 0;

            foreach (var path in dialog.FileNames)
            {
                var classified = App.Services.Classifier.Classify(path);
                if (classified.Format is not (DetectedFormat.Jpeg or DetectedFormat.Png or DetectedFormat.Webp))
                {
                    rejectedCount++;
                    continue;
                }

                if (_batchFiles.Any(b => string.Equals(b.File.Path, path, StringComparison.OrdinalIgnoreCase)))
                {
                    duplicateCount++;
                    continue;
                }

                _batchFiles.Add(new BatchQueueItemViewModel(classified));
                addedCount++;

                // Also add to workspace files
                App.Services.Queue.AddOrReplace(classified);
                if (!_files.Any(f => string.Equals(f.File.Path, path, StringComparison.OrdinalIgnoreCase)))
                {
                    _files.Add(new QueuedFileItem(classified));
                }
            }

            UpdateBatchQueueChrome();
            RefreshQueueChrome();

            if (addedCount > 0)
            {
                ShowBanner($"Added {addedCount} image(s) to batch queue.", error: false);
            }
            else if (duplicateCount > 0)
            {
                ShowBanner($"Selected files are already in the queue ({duplicateCount} duplicate(s) ignored).", error: false, warning: true);
            }
            else if (rejectedCount > 0)
            {
                ShowBanner($"Selected file(s) are not supported. Only JPEG, PNG, and WebP images can be processed.", error: true);
            }
        }
    }

    private void BatchClearQueue_Click(object sender, RoutedEventArgs e)
    {
        if (_isBatchRunning)
        {
            ShowBanner("Cannot clear queue while a batch is running.", error: true);
            return;
        }

        _batchFiles.Clear();
        BatchSelectedInspectorCard.Visibility = Visibility.Collapsed;
        BatchSummaryCard.Visibility = Visibility.Collapsed;
        BatchProgressCard.Visibility = Visibility.Collapsed;
        UpdateBatchQueueChrome();
        ShowBanner("Batch queue cleared. Original files on disk were not deleted.", error: false);
    }

    private void BatchRemoveItem_Click(object sender, RoutedEventArgs e)
    {
        if (_isBatchRunning) return;
        if (sender is Button { Tag: BatchQueueItemViewModel item })
        {
            _batchFiles.Remove(item);
            if (BatchFileList.SelectedItem == item)
            {
                BatchSelectedInspectorCard.Visibility = Visibility.Collapsed;
            }
            UpdateBatchQueueChrome();
        }
    }

    private void BatchOperationSelect_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string tag) return;

        _selectedBatchActionId = tag;

        // Update button active styles
        BatchOpCompressBtn.Style = (Style)FindResource(tag == ActionIds.CompressImage ? "PrimaryButton" : "SecondaryButton");
        BatchOpConvertBtn.Style = (Style)FindResource(tag == "image.convert" ? "PrimaryButton" : "SecondaryButton");
        BatchOpResizeBtn.Style = (Style)FindResource(tag == ActionIds.ResizeImage ? "PrimaryButton" : "SecondaryButton");
        BatchOpMetadataBtn.Style = (Style)FindResource(tag == ActionIds.RemoveImageMetadata ? "PrimaryButton" : "SecondaryButton");

        // Toggle panel visibilities
        BatchCompressSettingsPanel.Visibility = tag == ActionIds.CompressImage ? Visibility.Visible : Visibility.Collapsed;
        BatchConvertSettingsPanel.Visibility = tag == "image.convert" ? Visibility.Visible : Visibility.Collapsed;
        BatchResizeSettingsPanel.Visibility = tag == ActionIds.ResizeImage ? Visibility.Visible : Visibility.Collapsed;
        BatchMetadataSettingsPanel.Visibility = tag == ActionIds.RemoveImageMetadata ? Visibility.Visible : Visibility.Collapsed;

        BatchValidationErrorText.Visibility = Visibility.Collapsed;
    }

    private void BatchTargetFormat_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string tag) return;
        _selectedBatchTargetFormat = tag;

        BatchTargetWebpBtn.Style = (Style)FindResource(tag == "webp" ? "PrimaryButton" : "SecondaryButton");
        BatchTargetPngBtn.Style = (Style)FindResource(tag == "png" ? "PrimaryButton" : "SecondaryButton");
        BatchTargetJpegBtn.Style = (Style)FindResource(tag == "jpeg" ? "PrimaryButton" : "SecondaryButton");

        BatchConvertQualityPanel.Visibility = tag == "png" ? Visibility.Collapsed : Visibility.Visible;
        BatchConvertHintText.Text = tag switch
        {
            "webp" => "WebP offers high compression efficiency with alpha channel support.",
            "png" => "PNG is lossless and preserves transparency. Recompressed with zlib.",
            "jpeg" => "JPEG provides high compatibility. Note that transparency is flattened to opaque pixels.",
            _ => ""
        };
    }

    private void BatchCompressQualitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (BatchCompressQualityBadge is not null)
        {
            BatchCompressQualityBadge.Text = $"{(int)e.NewValue}%";
        }
    }

    private void BatchConvertQualitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (BatchConvertQualityBadge is not null)
        {
            BatchConvertQualityBadge.Text = $"{(int)e.NewValue}%";
        }
    }

    private void BatchPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && double.TryParse(tag, out var val))
        {
            BatchCompressQualitySlider.Value = val;
        }
    }

    private void BatchResizePreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag })
        {
            BatchResizeWidthBox.Text = tag;
            BatchResizeHeightBox.Text = tag;
        }
    }

    private async void StartBatchProcess_Click(object sender, RoutedEventArgs e)
    {
        if (_isBatchRunning || _batchFiles.Count == 0) return;

        string effectiveActionId = _selectedBatchActionId;
        var optionsDict = new Dictionary<string, string>();

        if (_selectedBatchActionId == ActionIds.CompressImage)
        {
            optionsDict["quality"] = ((int)BatchCompressQualitySlider.Value).ToString();
        }
        else if (_selectedBatchActionId == "image.convert")
        {
            effectiveActionId = _selectedBatchTargetFormat switch
            {
                "png" => ActionIds.ConvertToPng,
                "jpeg" => ActionIds.ConvertToJpeg,
                _ => ActionIds.ConvertToWebp
            };

            if (_selectedBatchTargetFormat != "png")
            {
                optionsDict["quality"] = ((int)BatchConvertQualitySlider.Value).ToString();
            }
        }
        else if (_selectedBatchActionId == ActionIds.ResizeImage)
        {
            if (!int.TryParse(BatchResizeWidthBox.Text, out int w) || w <= 0 ||
                !int.TryParse(BatchResizeHeightBox.Text, out int h) || h <= 0)
            {
                BatchValidationErrorText.Text = "Resize dimensions must be positive integers.";
                BatchValidationErrorText.Visibility = Visibility.Visible;
                return;
            }

            if (ImageSafetyLimits.Exceeds(w, h))
            {
                BatchValidationErrorText.Text = $"Target dimensions exceed safety limit (max {ImageSafetyLimits.MaxDimension}px per side).";
                BatchValidationErrorText.Visibility = Visibility.Visible;
                return;
            }

            optionsDict["width"] = w.ToString();
            optionsDict["height"] = h.ToString();
            optionsDict["keepAspect"] = (BatchResizeKeepAspectCheck.IsChecked == true).ToString();
        }
        else if (_selectedBatchActionId == ActionIds.RemoveImageMetadata)
        {
            // No extra options required
        }

        BatchValidationErrorText.Visibility = Visibility.Collapsed;

        int concurrency = (BatchParallelRadio.IsChecked == true) ? 2 : 1;
        var filesToProcess = _batchFiles.Select(b => b.File).ToList();
        var request = new BatchJobRequest(
            filesToProcess,
            effectiveActionId,
            new ProcessingOptions(optionsDict),
            App.Services.Outputs.RootDirectory,
            concurrency);

        var validationErrors = App.Services.BatchEngine.ValidateRequest(request);
        if (validationErrors.Count > 0)
        {
            BatchValidationErrorText.Text = string.Join(" ", validationErrors);
            BatchValidationErrorText.Visibility = Visibility.Visible;
            return;
        }

        // Reset UI for run
        _isBatchRunning = true;
        BatchStartButton.IsEnabled = false;
        BatchCancelButton.IsEnabled = true;
        BatchProgressCard.Visibility = Visibility.Visible;
        BatchSummaryCard.Visibility = Visibility.Collapsed;
        BatchProgressBar.Value = 0;
        BatchProgressPercentText.Text = "0%";
        BatchProgressStatusText.Text = "Starting batch…";
        BatchProgressSucceededText.Text = "✓ 0 Succeeded";
        BatchProgressFailedText.Text = "✕ 0 Failed";
        BatchProgressCancelledText.Text = "⊘ 0 Cancelled";

        foreach (var item in _batchFiles)
        {
            item.Status = BatchItemStatus.Pending;
            item.StatusText = "Queued";
            item.OutputPath = null;
            item.ResultMessage = null;
        }

        _batchCts?.Cancel();
        _batchCts = new CancellationTokenSource();
        var token = _batchCts.Token;

        var progress = new Progress<BatchProgressUpdate>(update =>
        {
            Dispatcher.InvokeAsync(() =>
            {
                BatchProgressBar.Value = update.OverallPercent;
                BatchProgressPercentText.Text = $"{update.OverallPercent}%";
                BatchProgressStatusText.Text = update.StatusMessage ?? "";
                BatchProgressSucceededText.Text = $"✓ {update.SucceededCount} Succeeded";
                BatchProgressFailedText.Text = $"✕ {update.FailedCount} Failed";
                BatchProgressCancelledText.Text = $"⊘ {update.CancelledCount} Cancelled";

                if (!string.IsNullOrWhiteSpace(update.CurrentFileName))
                {
                    var activeItem = _batchFiles.FirstOrDefault(b => string.Equals(b.FileName, update.CurrentFileName, StringComparison.OrdinalIgnoreCase));
                    if (activeItem is not null && activeItem.Status == BatchItemStatus.Pending)
                    {
                        activeItem.Status = BatchItemStatus.Processing;
                        activeItem.StatusText = "Processing…";
                    }
                }
            });
        });

        try
        {
            var summary = await App.Services.BatchEngine.ProcessBatchAsync(request, progress, token).ConfigureAwait(true);

            // Update individual item views
            for (int i = 0; i < summary.ItemResults.Count && i < _batchFiles.Count; i++)
            {
                var res = summary.ItemResults[i];
                var item = _batchFiles[i];

                item.Status = res.Status;
                item.StatusText = res.Status switch
                {
                    BatchItemStatus.Succeeded => "✓ Success",
                    BatchItemStatus.Failed => "✕ Failed",
                    BatchItemStatus.Cancelled => "Cancelled",
                    _ => res.Status.ToString()
                };
                item.OutputPath = res.OutputPath;
                item.ResultMessage = res.StatusMessage;
                item.Details = res.Details;
            }

            // Show summary card
            BatchSummaryCard.Visibility = Visibility.Visible;
            BatchSummaryTitleText.Text = summary.SummaryMessage;
            BatchSummaryDetailText.Text = $"{summary.SucceededCount} succeeded, {summary.FailedCount} failed, {summary.CancelledCount} cancelled in {summary.Elapsed.TotalSeconds:F1}s.";

            if (summary.AnySucceeded)
            {
                _lastOutputPath = summary.ItemResults.FirstOrDefault(r => r.Status == BatchItemStatus.Succeeded)?.OutputPath;
            }

            ShowBanner(summary.SummaryMessage, error: !summary.AllSucceeded && !summary.AnySucceeded, warning: !summary.AllSucceeded && summary.AnySucceeded);
        }
        catch (OperationCanceledException)
        {
            ShowBanner("Batch operation was cancelled.", error: false, warning: true);
        }
        catch (Exception ex)
        {
            ShowBanner($"Batch error: {ex.Message}", error: true);
        }
        finally
        {
            _isBatchRunning = false;
            BatchStartButton.IsEnabled = _batchFiles.Count > 0;
            BatchCancelButton.IsEnabled = false;
            BatchProgressBar.Value = 100;
        }
    }

    private void CancelBatchProcess_Click(object sender, RoutedEventArgs e)
    {
        if (_isBatchRunning && _batchCts is not null)
        {
            BatchProgressStatusText.Text = "Cancelling batch…";
            BatchCancelButton.IsEnabled = false;
            _batchCts.Cancel();
        }
    }

    private void BatchFileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BatchFileList.SelectedItem is BatchQueueItemViewModel item)
        {
            BatchSelectedInspectorCard.Visibility = Visibility.Visible;
            BatchInspectorFileName.Text = item.FileName;
            BatchInspectorStatusMessage.Text = item.ResultMessage ?? $"Status: {item.StatusText}";

            if (item.CanReveal)
            {
                BatchInspectorOutputBlock.Visibility = Visibility.Visible;
                BatchInspectorOutputPath.Text = item.OutputPath;
            }
            else
            {
                BatchInspectorOutputBlock.Visibility = Visibility.Collapsed;
            }
        }
        else
        {
            BatchSelectedInspectorCard.Visibility = Visibility.Collapsed;
        }
    }

    private void BatchInspectorOpenFile_Click(object sender, RoutedEventArgs e)
    {
        if (BatchFileList.SelectedItem is BatchQueueItemViewModel { CanReveal: true } item)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(item.OutputPath!) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                ShowBanner($"Failed to open file: {ex.Message}", error: true);
            }
        }
    }

    private void BatchInspectorReveal_Click(object sender, RoutedEventArgs e)
    {
        if (BatchFileList.SelectedItem is BatchQueueItemViewModel { CanReveal: true } item)
        {
            try
            {
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{item.OutputPath!}\"");
            }
            catch (Exception ex)
            {
                ShowBanner($"Failed to reveal file: {ex.Message}", error: true);
            }
        }
    }

    // ==========================================
    // MULTI-IMAGE DOCUMENT BUILDER STUDIO
    // ==========================================

    private void OpenDocBuilder_Click(object sender, RoutedEventArgs e) => OpenDocumentBuilder();

    private void CloseDocBuilder_Click(object sender, RoutedEventArgs e) => CloseDocumentBuilder();

    public void OpenDocumentBuilder()
    {
        if (ImageEditorHost.Visibility == Visibility.Visible)
        {
            CloseImageEditor();
        }
        if (BatchStudioHost.Visibility == Visibility.Visible)
        {
            CloseBatchStudio();
        }
        ClosePdfPreview();

        DocumentBuilderHost.Visibility = Visibility.Visible;
        NavDocBuilderBtn.Tag = "Active";
        NavWorkspaceBtn.Tag = null;
        NavQueueBtn.Tag = null;

        // Auto-populate from workspace files if empty
        if (_docBuilderFiles.Count == 0 && _files.Count > 0)
        {
            foreach (var item in _files)
            {
                if (item.File.Format is DetectedFormat.Jpeg or DetectedFormat.Png or DetectedFormat.Webp)
                {
                    if (!_docBuilderFiles.Any(b => string.Equals(b.File.Path, item.File.Path, StringComparison.OrdinalIgnoreCase)))
                    {
                        _docBuilderFiles.Add(new DocBuilderItemViewModel(item.File, _docBuilderFiles.Count + 1));
                    }
                }
            }
        }

        UpdateDocBuilderSequence();
        UpdateDocBuilderQueueChrome();
        UpdateDocumentPreview();
    }

    public void CloseDocumentBuilder()
    {
        if (_isDocBuilding)
        {
            ShowBanner("A document is currently being generated. Please wait for completion.", error: false, warning: true);
            return;
        }

        DocumentBuilderHost.Visibility = Visibility.Collapsed;
        NavDocBuilderBtn.Tag = null;
        NavWorkspaceBtn.Tag = "Active";
    }

    private void UpdateDocBuilderSequence()
    {
        for (int i = 0; i < _docBuilderFiles.Count; i++)
        {
            var item = _docBuilderFiles[i];
            item.PositionIndex = i + 1;
            item.CanMoveUp = i > 0;
            item.CanMoveDown = i < _docBuilderFiles.Count - 1;
        }
    }

    private void UpdateDocBuilderQueueChrome()
    {
        int count = _docBuilderFiles.Count;
        DocBuilderCountText.Text = $"{count} image{(count == 1 ? "" : "s")}";
        DocBuilderEmptyPlaceholder.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DocBuilderFileList.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        DocBuilderExportBtn.IsEnabled = count > 0 && !_isDocBuilding;

        DocBuilderPreviewMetaText.Text = _docBuilderMode == DocumentOutputMode.Pdf
            ? $"PDF • {_docPageSize} {_docPageOrientation} • {_docPdfPlacement}"
            : $"PPTX • {_docSlideSize} • {_docPptxPlacement}";
    }

    private void DocBuilderMoveUp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: DocBuilderItemViewModel item })
        {
            int idx = _docBuilderFiles.IndexOf(item);
            if (idx > 0)
            {
                _docBuilderFiles.Move(idx, idx - 1);
                UpdateDocBuilderSequence();
                _docBuilderPreviewIndex = idx - 1;
                UpdateDocumentPreview();
            }
        }
    }

    private void DocBuilderMoveDown_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: DocBuilderItemViewModel item })
        {
            int idx = _docBuilderFiles.IndexOf(item);
            if (idx >= 0 && idx < _docBuilderFiles.Count - 1)
            {
                _docBuilderFiles.Move(idx, idx + 1);
                UpdateDocBuilderSequence();
                _docBuilderPreviewIndex = idx + 1;
                UpdateDocumentPreview();
            }
        }
    }

    private void DocBuilderRemoveItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: DocBuilderItemViewModel item })
        {
            _docBuilderFiles.Remove(item);
            UpdateDocBuilderSequence();
            UpdateDocBuilderQueueChrome();
            if (_docBuilderPreviewIndex >= _docBuilderFiles.Count)
            {
                _docBuilderPreviewIndex = Math.Max(0, _docBuilderFiles.Count - 1);
            }
            UpdateDocumentPreview();
        }
    }

    private void DocBuilderClearCollection_Click(object sender, RoutedEventArgs e)
    {
        _docBuilderFiles.Clear();
        _docBuilderPreviewIndex = 0;
        UpdateDocBuilderQueueChrome();
        UpdateDocumentPreview();
    }

    private void DocBuilderAddImages_Click(object sender, RoutedEventArgs e)
    {
        if (_isDocBuilding)
        {
            ShowBanner("Cannot add files while document generation is running.", error: true);
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Multiselect = true,
            Title = "Select Images to Add to Document",
            Filter = "Image Files (*.jpg;*.jpeg;*.png;*.webp)|*.jpg;*.jpeg;*.png;*.webp|All Files (*.*)|*.*"
        };

        if (dialog.ShowDialog() == true && dialog.FileNames.Length > 0)
        {
            int addedCount = 0;
            int duplicateCount = 0;

            foreach (var path in dialog.FileNames)
            {
                if (!File.Exists(path)) continue;
                if (_docBuilderFiles.Any(f => string.Equals(f.File.Path, path, StringComparison.OrdinalIgnoreCase)))
                {
                    duplicateCount++;
                    continue;
                }

                var classified = App.Services.Classifier.Classify(path);
                if (classified.Format is DetectedFormat.Jpeg or DetectedFormat.Png or DetectedFormat.Webp)
                {
                    _docBuilderFiles.Add(new DocBuilderItemViewModel(classified, _docBuilderFiles.Count + 1));
                    addedCount++;
                }
            }

            UpdateDocBuilderSequence();
            UpdateDocBuilderQueueChrome();
            UpdateDocumentPreview();

            if (addedCount > 0)
            {
                ShowBanner($"Added {addedCount} image(s) to document builder.", error: false);
            }
            else if (duplicateCount > 0)
            {
                ShowBanner($"Selected images are already in the document ({duplicateCount} duplicate(s) ignored).", error: false, warning: true);
            }
        }
    }

    private void DocBuilderFileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DocBuilderFileList.SelectedItem is DocBuilderItemViewModel item)
        {
            int idx = _docBuilderFiles.IndexOf(item);
            if (idx >= 0)
            {
                _docBuilderPreviewIndex = idx;
                UpdateDocumentPreview();
            }
        }
    }

    private void DocBuilderModePdf_Click(object sender, RoutedEventArgs e)
    {
        _docBuilderMode = DocumentOutputMode.Pdf;
        DocBuilderModePdfBtn.Style = (Style)FindResource("PrimaryButton");
        DocBuilderModePptxBtn.Style = (Style)FindResource("SecondaryButton");
        DocBuilderPdfSettingsPanel.Visibility = Visibility.Visible;
        DocBuilderPptxSettingsPanel.Visibility = Visibility.Collapsed;
        DocBuilderExportBtnText.Text = "Build PDF";
        UpdateDocBuilderQueueChrome();
        UpdateDocumentPreview();
    }

    private void DocBuilderModePptx_Click(object sender, RoutedEventArgs e)
    {
        _docBuilderMode = DocumentOutputMode.Pptx;
        DocBuilderModePdfBtn.Style = (Style)FindResource("SecondaryButton");
        DocBuilderModePptxBtn.Style = (Style)FindResource("PrimaryButton");
        DocBuilderPdfSettingsPanel.Visibility = Visibility.Collapsed;
        DocBuilderPptxSettingsPanel.Visibility = Visibility.Visible;
        DocBuilderExportBtnText.Text = "Build PPTX";
        UpdateDocBuilderQueueChrome();
        UpdateDocumentPreview();
    }

    private void DocPdfSize_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && Enum.TryParse<PageSizeOption>(tag, out var size))
        {
            _docPageSize = size;
            DocPdfSizeA4Btn.Style = (Style)FindResource(size == PageSizeOption.A4 ? "PrimaryButton" : "SecondaryButton");
            DocPdfSizeLetterBtn.Style = (Style)FindResource(size == PageSizeOption.Letter ? "PrimaryButton" : "SecondaryButton");
            DocPdfSizeMatchBtn.Style = (Style)FindResource(size == PageSizeOption.ImageMatch ? "PrimaryButton" : "SecondaryButton");
            UpdateDocBuilderQueueChrome();
            UpdateDocumentPreview();
        }
    }

    private void DocPdfOrient_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && Enum.TryParse<PageOrientationOption>(tag, out var orient))
        {
            _docPageOrientation = orient;
            DocPdfOrientPortraitBtn.Style = (Style)FindResource(orient == PageOrientationOption.Portrait ? "PrimaryButton" : "SecondaryButton");
            DocPdfOrientLandscapeBtn.Style = (Style)FindResource(orient == PageOrientationOption.Landscape ? "PrimaryButton" : "SecondaryButton");
            DocPdfOrientAutoBtn.Style = (Style)FindResource(orient == PageOrientationOption.Auto ? "PrimaryButton" : "SecondaryButton");
            UpdateDocBuilderQueueChrome();
            UpdateDocumentPreview();
        }
    }

    private void DocPdfPlacement_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && Enum.TryParse<ImagePlacementOption>(tag, out var placement))
        {
            _docPdfPlacement = placement;
            DocPdfPlacementContainBtn.Style = (Style)FindResource(placement == ImagePlacementOption.Contain ? "PrimaryButton" : "SecondaryButton");
            DocPdfPlacementFillBtn.Style = (Style)FindResource(placement == ImagePlacementOption.FillCrop ? "PrimaryButton" : "SecondaryButton");
            UpdateDocBuilderQueueChrome();
            UpdateDocumentPreview();
        }
    }

    private void DocPdfMargin_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && int.TryParse(tag, out var margin))
        {
            _docPdfMarginPoints = margin;
            DocPdfMargin0Btn.Style = (Style)FindResource(margin == 0 ? "PrimaryButton" : "SecondaryButton");
            DocPdfMargin18Btn.Style = (Style)FindResource(margin == 18 ? "PrimaryButton" : "SecondaryButton");
            DocPdfMargin36Btn.Style = (Style)FindResource(margin == 36 ? "PrimaryButton" : "SecondaryButton");
            DocPdfMargin72Btn.Style = (Style)FindResource(margin == 72 ? "PrimaryButton" : "SecondaryButton");
            UpdateDocBuilderQueueChrome();
            UpdateDocumentPreview();
        }
    }

    private void DocPdfBg_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string hex })
        {
            _docPdfBackgroundHex = hex;
            DocPdfBgWhiteBtn.Style = (Style)FindResource(string.Equals(hex, "#FFFFFF", StringComparison.OrdinalIgnoreCase) ? "PrimaryButton" : "SecondaryButton");
            DocPdfBgBlackBtn.Style = (Style)FindResource(string.Equals(hex, "#000000", StringComparison.OrdinalIgnoreCase) ? "PrimaryButton" : "SecondaryButton");
            UpdateDocBuilderQueueChrome();
            UpdateDocumentPreview();
        }
    }

    private void DocPptxSize_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && Enum.TryParse<SlideSizeOption>(tag, out var size))
        {
            _docSlideSize = size;
            DocPptxSize16x9Btn.Style = (Style)FindResource(size == SlideSizeOption.Widescreen16x9 ? "PrimaryButton" : "SecondaryButton");
            DocPptxSize4x3Btn.Style = (Style)FindResource(size == SlideSizeOption.Standard4x3 ? "PrimaryButton" : "SecondaryButton");
            DocPptxSizeMatchBtn.Style = (Style)FindResource(size == SlideSizeOption.ImageMatch ? "PrimaryButton" : "SecondaryButton");
            UpdateDocBuilderQueueChrome();
            UpdateDocumentPreview();
        }
    }

    private void DocPptxPlacement_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && Enum.TryParse<ImagePlacementOption>(tag, out var placement))
        {
            _docPptxPlacement = placement;
            DocPptxPlacementContainBtn.Style = (Style)FindResource(placement == ImagePlacementOption.Contain ? "PrimaryButton" : "SecondaryButton");
            DocPptxPlacementFillBtn.Style = (Style)FindResource(placement == ImagePlacementOption.FillCrop ? "PrimaryButton" : "SecondaryButton");
            UpdateDocBuilderQueueChrome();
            UpdateDocumentPreview();
        }
    }

    private void DocPptxBg_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string hex })
        {
            _docPptxBackgroundHex = hex;
            DocPptxBgWhiteBtn.Style = (Style)FindResource(string.Equals(hex, "#FFFFFF", StringComparison.OrdinalIgnoreCase) ? "PrimaryButton" : "SecondaryButton");
            DocPptxBgBlackBtn.Style = (Style)FindResource(string.Equals(hex, "#000000", StringComparison.OrdinalIgnoreCase) ? "PrimaryButton" : "SecondaryButton");
            UpdateDocBuilderQueueChrome();
            UpdateDocumentPreview();
        }
    }

    private void DocBuilderPrevPage_Click(object sender, RoutedEventArgs e)
    {
        if (_docBuilderPreviewIndex > 0)
        {
            _docBuilderPreviewIndex--;
            UpdateDocumentPreview();
        }
    }

    private void DocBuilderNextPage_Click(object sender, RoutedEventArgs e)
    {
        if (_docBuilderPreviewIndex < _docBuilderFiles.Count - 1)
        {
            _docBuilderPreviewIndex++;
            UpdateDocumentPreview();
        }
    }

    private async void UpdateDocumentPreview()
    {
        if (_docBuilderFiles.Count == 0)
        {
            DocBuilderPreviewImage.Source = null;
            DocBuilderPreviewPlaceholder.Visibility = Visibility.Visible;
            DocBuilderPageIndicatorText.Text = "0 of 0";
            DocBuilderPrevPageBtn.IsEnabled = false;
            DocBuilderNextPageBtn.IsEnabled = false;
            return;
        }

        DocBuilderPreviewPlaceholder.Visibility = Visibility.Collapsed;
        _docBuilderPreviewIndex = Math.Clamp(_docBuilderPreviewIndex, 0, _docBuilderFiles.Count - 1);
        DocBuilderPageIndicatorText.Text = $"{(_docBuilderMode == DocumentOutputMode.Pdf ? "Page" : "Slide")} {_docBuilderPreviewIndex + 1} of {_docBuilderFiles.Count}";
        DocBuilderPrevPageBtn.IsEnabled = _docBuilderPreviewIndex > 0;
        DocBuilderNextPageBtn.IsEnabled = _docBuilderPreviewIndex < _docBuilderFiles.Count - 1;

        var targetFile = _docBuilderFiles[_docBuilderPreviewIndex].File;
        var mode = _docBuilderMode;
        var pageSettings = new DocumentPageSettings(
            PageSize: _docPageSize,
            Orientation: _docPageOrientation,
            Placement: _docPdfPlacement,
            MarginPoints: _docPdfMarginPoints,
            BackgroundHex: _docPdfBackgroundHex);
        var slideSettings = new DocumentSlideSettings(
            SlideSize: _docSlideSize,
            Placement: _docPptxPlacement,
            BackgroundHex: _docPptxBackgroundHex);

        try
        {
            var previewBitmap = await Task.Run(() => App.Services.DocumentBuilder.RenderPreview(
                targetFile,
                mode,
                pageSettings,
                slideSettings,
                800,
                600));

            if (previewBitmap is not null)
            {
                using (previewBitmap)
                {
                    var bmp = BitmapSource.Create(
                        previewBitmap.Width,
                        previewBitmap.Height,
                        96,
                        96,
                        PixelFormats.Bgra32,
                        null,
                        previewBitmap.GetPixels(),
                        previewBitmap.ByteCount,
                        previewBitmap.RowBytes);
                    bmp.Freeze();
                    DocBuilderPreviewImage.Source = bmp;
                }
            }
        }
        catch (Exception ex)
        {
            App.Logger.Error("docbuilder", $"Failed to render preview for {targetFile.FileName}", ex);
        }
    }

    private async void ExportDocument_Click(object sender, RoutedEventArgs e)
    {
        if (_isDocBuilding || _docBuilderFiles.Count == 0) return;

        _isDocBuilding = true;
        DocBuilderProgressBlock.Visibility = Visibility.Visible;
        DocBuilderResultCard.Visibility = Visibility.Collapsed;
        DocBuilderProgressBar.Value = 0;
        DocBuilderStatusText.Text = "Preparing document...";
        DocBuilderExportBtn.IsEnabled = false;

        _docBuilderCts = new CancellationTokenSource();
        var token = _docBuilderCts.Token;

        var customName = DocBuilderDocNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(customName))
        {
            customName = "SmartShift-Document";
        }

        var files = _docBuilderFiles.Select(f => f.File).ToList();
        var mode = _docBuilderMode;
        var pageSettings = new DocumentPageSettings(
            PageSize: _docPageSize,
            Orientation: _docPageOrientation,
            Placement: _docPdfPlacement,
            MarginPoints: _docPdfMarginPoints,
            BackgroundHex: _docPdfBackgroundHex);
        var slideSettings = new DocumentSlideSettings(
            SlideSize: _docSlideSize,
            Placement: _docPptxPlacement,
            BackgroundHex: _docPptxBackgroundHex);

        var request = new MultiImageDocumentRequest(
            Files: files,
            Mode: mode,
            PageSettings: pageSettings,
            SlideSettings: slideSettings,
            OutputDirectory: App.Services.Outputs.RootDirectory,
            CustomFileName: customName);

        var progress = new Progress<ProcessingProgress>(p =>
        {
            Dispatcher.Invoke(() =>
            {
                DocBuilderProgressBar.Value = p.Percent;
                DocBuilderStatusText.Text = p.Message;
            });
        });

        try
        {
            var result = await Task.Run(() => App.Services.DocumentBuilder.BuildDocumentAsync(request, progress, token));

            if (result.Succeeded && result.OutputPath is not null)
            {
                _lastGeneratedDocumentPath = result.OutputPath;
                DocBuilderResultCard.Visibility = Visibility.Visible;
                DocBuilderResultPathText.Text = result.OutputPath;
                ShowBanner($"Successfully generated {Path.GetFileName(result.OutputPath)} ({result.PageOrSlideCount} {(mode == DocumentOutputMode.Pdf ? "pages" : "slides")}). Originals were not modified.", error: false);

                // Ingest into SmartShift workspace queue
                if (File.Exists(result.OutputPath))
                {
                    await IngestAsync([result.OutputPath]);
                }
            }
            else
            {
                ShowBanner($"Document generation failed: {result.StatusMessage}", error: true);
            }
        }
        catch (OperationCanceledException)
        {
            ShowBanner("Document generation was cancelled.", error: false, warning: true);
        }
        catch (Exception ex)
        {
            App.Logger.Error("docbuilder", "Unexpected error during document generation", ex);
            ShowBanner($"Error: {ex.Message}", error: true);
        }
        finally
        {
            _isDocBuilding = false;
            DocBuilderProgressBlock.Visibility = Visibility.Collapsed;
            DocBuilderExportBtn.IsEnabled = true;
        }
    }

    private void DocBuilderOpenResult_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_lastGeneratedDocumentPath) && File.Exists(_lastGeneratedDocumentPath))
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = _lastGeneratedDocumentPath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                ShowBanner($"Could not open document: {ex.Message}", error: true);
            }
        }
    }

    private void DocBuilderRevealResult_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_lastGeneratedDocumentPath) && File.Exists(_lastGeneratedDocumentPath))
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{_lastGeneratedDocumentPath}\"",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                ShowBanner($"Could not reveal file in folder: {ex.Message}", error: true);
            }
        }
    }
}
