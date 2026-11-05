using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using SmartShift.App.Ui;
using SmartShift.Core.Classification;
using SmartShift.Core.History;
using SmartShift.Processing;
using SmartShift.Processing.Pdf;
using SmartShift.Processing.Pdf.Preview;
using SmartShift.Windows.DragDrop;
using SmartShift.Windows.Shell;

namespace SmartShift.App;

public partial class MainWindow : Window
{
    private readonly PdfPageOrganizerSession _organizeSession = new();
    private int _pdfOrganizeCurrentPreviewIndex = 0;
    private CancellationTokenSource? _pdfOrganizeCts;
    private CancellationTokenSource? _thumbnailLoadingCts;
    private bool _isPdfOrganizing = false;
    private string? _lastOrganizedPdfPath;
    private Point _cardDragStartPoint;
    private PdfOrganizeItemViewModel? _draggedCardItem;
    private double _organizeZoomLevel = 1.0;
    private readonly Dictionary<int, ImageSource> _thumbnailCache = [];

    private void InitializePdfPageOrganizerStudio()
    {
        if (PdfOrganizePagesList is not null)
        {
            PdfOrganizePagesList.ItemsSource = _organizeSession.Pages;
        }

        _organizeSession.StateChanged += (_, _) =>
        {
            Dispatcher.Invoke(UpdateOrganizeUi);
        };

        if (PdfOrganizeOutputDirBox is not null && string.IsNullOrWhiteSpace(PdfOrganizeOutputDirBox.Text))
        {
            PdfOrganizeOutputDirBox.Text = App.Services.Outputs.RootDirectory;
        }
    }

    public async void OpenPdfOrganizeStudio(string? targetPdfPath = null)
    {
        if (ImageEditorHost.Visibility == Visibility.Visible) CloseImageEditor();
        if (BatchStudioHost.Visibility == Visibility.Visible) CloseBatchStudio();
        if (DocumentBuilderHost.Visibility == Visibility.Visible) CloseDocumentBuilder();
        if (PresentationStudioHost.Visibility == Visibility.Visible) ClosePresentationStudio();
        if (PdfMergeStudioHost.Visibility == Visibility.Visible) ClosePdfMergeStudio();
        if (PdfSplitStudioHost.Visibility == Visibility.Visible) ClosePdfSplitStudio();
        ClosePdfPreview();

        PdfOrganizeStudioHost.Visibility = Visibility.Visible;
        NavPdfOrganizeBtn.Tag = "Active";
        NavWorkspaceBtn.Tag = null;
        NavQueueBtn.Tag = null;
        NavDocBuilderBtn.Tag = null;
        NavPresentationBtn.Tag = null;
        NavPdfMergeBtn.Tag = null;
        NavPdfSplitBtn.Tag = null;

        // Determine path to load
        string? pathToLoad = targetPdfPath;
        if (string.IsNullOrWhiteSpace(pathToLoad) && _selectedFileItem is not null && _selectedFileItem.File.Format == DetectedFormat.Pdf)
        {
            pathToLoad = _selectedFileItem.File.Path;
        }

        if (!string.IsNullOrWhiteSpace(pathToLoad) && File.Exists(pathToLoad))
        {
            await LoadPdfOrganizeDocumentAsync(pathToLoad);
        }
        else
        {
            ShowEmptyPdfOrganizeState("Select or open a PDF document to begin organizing.");
        }
    }

    public void ClosePdfOrganizeStudio()
    {
        if (_isPdfOrganizing && _pdfOrganizeCts is not null)
        {
            _pdfOrganizeCts.Cancel();
        }

        _thumbnailLoadingCts?.Cancel();
        _thumbnailLoadingCts = null;

        PdfOrganizeStudioHost.Visibility = Visibility.Collapsed;
        NavPdfOrganizeBtn.Tag = null;
    }

    private void NavPdfOrganize_Click(object sender, RoutedEventArgs e)
    {
        if (PdfOrganizeStudioHost.Visibility == Visibility.Visible)
        {
            ClosePdfOrganizeStudio();
        }
        else
        {
            OpenPdfOrganizeStudio();
        }
    }

    private void ClosePdfOrganizeStudio_Click(object sender, RoutedEventArgs e)
    {
        ClosePdfOrganizeStudio();
    }

    private void ShowEmptyPdfOrganizeState(string message)
    {
        PdfOrganizeHeaderFileName.Text = "No Document Selected";
        PdfOrganizeHeaderFileSize.Text = "—";
        PdfOrganizeHeaderPageCount.Text = "0 pages";
        PdfOrganizeHeaderSelectionCount.Text = "0 selected";
        PdfOrganizeHeaderModifiedText.Text = "No document";
        PdfOrganizeHeaderStatus.Text = message;
        PdfOrganizePreviewPageIndicator.Text = "Page 0 of 0";
        PdfOrganizePreviewDimensionsText.Text = "Dimensions: —";
        PdfOrganizeLargePreviewImage.Source = null;
        _organizeSession.Pages.Clear();
        UpdateOrganizeUi();
    }

    public async Task LoadPdfOrganizeDocumentAsync(string filePath)
    {
        _thumbnailLoadingCts?.Cancel();
        _thumbnailLoadingCts = new CancellationTokenSource();
        var token = _thumbnailLoadingCts.Token;

        _thumbnailCache.Clear();
        PdfOrganizeSuccessBanner.Visibility = Visibility.Collapsed;
        PdfOrganizeProgressBar.Visibility = Visibility.Collapsed;
        PdfOrganizeProgressText.Visibility = Visibility.Collapsed;

        var validation = await App.Services.PdfOrganize.ValidateSourceAsync(filePath, token);
        if (!validation.IsValid)
        {
            ShowEmptyPdfOrganizeState(validation.ErrorReason ?? "The selected file is not a valid PDF document.");
            return;
        }

        _organizeSession.Initialize(
            validation.FilePath,
            validation.TotalPages,
            validation.FileSizeBytes,
            validation.PageRotations ?? [],
            validation.PageDimensions ?? []);

        PdfOrganizeHeaderFileName.Text = validation.FileName;
        PdfOrganizeHeaderFileSize.Text = FormatFileSize(validation.FileSizeBytes);
        PdfOrganizeHeaderStatus.Text = $"Loaded {validation.TotalPages} page(s). Reorder, rotate, or delete pages before export.";

        string baseName = Path.GetFileNameWithoutExtension(validation.FilePath);
        PdfOrganizeOutputFileNameBox.Text = $"{baseName}_organized.pdf";

        if (string.IsNullOrWhiteSpace(PdfOrganizeOutputDirBox.Text))
        {
            PdfOrganizeOutputDirBox.Text = App.Services.Outputs.RootDirectory;
        }

        _pdfOrganizeCurrentPreviewIndex = 0;
        UpdateOrganizeUi();
        UpdateLargePreview();

        // Load thumbnails in the background
        _ = LoadThumbnailsAsync(filePath, validation.TotalPages, token);
    }

    private async Task LoadThumbnailsAsync(string filePath, int totalPages, CancellationToken token)
    {
        for (int i = 0; i < totalPages; i++)
        {
            if (token.IsCancellationRequested) return;

            try
            {
                var pageData = await PdfPreviewRenderer.RenderPageAsync(filePath, i, 72.0, token);
                if (pageData != null && !token.IsCancellationRequested)
                {
                    var bmp = BitmapSource.Create(
                        pageData.PixelWidth,
                        pageData.PixelHeight,
                        96,
                        96,
                        PixelFormats.Bgra32,
                        null,
                        pageData.BgraBytes,
                        pageData.Stride);
                    bmp.Freeze();

                    _thumbnailCache[i] = bmp;

                    Dispatcher.Invoke(() =>
                    {
                        foreach (var page in _organizeSession.Pages)
                        {
                            if (page.OriginalPageIndex == i)
                            {
                                page.Thumbnail = bmp;
                            }
                        }

                        // If current large preview is this page and doesn't have an image yet
                        if (_organizeSession.Pages.Count > _pdfOrganizeCurrentPreviewIndex &&
                            _organizeSession.Pages[_pdfOrganizeCurrentPreviewIndex].OriginalPageIndex == i &&
                            PdfOrganizeLargePreviewImage.Source == null)
                        {
                            UpdateLargePreview();
                        }
                    });
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                // Soft fail on thumbnail load
            }
        }
    }

    private void UpdateOrganizeUi()
    {
        int pageCount = _organizeSession.Pages.Count;
        int selCount = _organizeSession.SelectedCount;

        PdfOrganizeHeaderPageCount.Text = $"{pageCount} page{(pageCount == 1 ? "" : "s")}";
        PdfOrganizeHeaderSelectionCount.Text = $"{selCount} selected";

        bool hasModifications = _organizeSession.HasUnsavedChanges;
        PdfOrganizeHeaderModifiedText.Text = hasModifications ? "Unsaved changes" : "Original state";
        PdfOrganizeHeaderModifiedBadge.Background = hasModifications
            ? (Brush)FindResource("WarnSoftBrush")
            : (Brush)FindResource("GlassCardBrush");
        PdfOrganizeHeaderModifiedText.Foreground = hasModifications
            ? (Brush)FindResource("WarnBrush")
            : (Brush)FindResource("TextBrush");

        PdfOrganizeHeaderResetBtn.IsEnabled = hasModifications;
        PdfOrganizeResetBtn.IsEnabled = hasModifications;

        PdfOrganizeSelectAllBtn.IsEnabled = pageCount > 0;
        PdfOrganizeClearSelBtn.IsEnabled = selCount > 0;

        PdfOrganizeMoveLeftBtn.IsEnabled = _organizeSession.CanMoveLeft;
        PdfOrganizeMoveRightBtn.IsEnabled = _organizeSession.CanMoveRight;

        PdfOrganizeRotateCcwBtn.IsEnabled = selCount > 0;
        PdfOrganizeRotateCwBtn.IsEnabled = selCount > 0;
        PdfOrganizeRotate180Btn.IsEnabled = selCount > 0;

        PdfOrganizeDeleteBtn.IsEnabled = _organizeSession.CanDeleteSelected;

        PdfOrganizeUndoBtn.IsEnabled = _organizeSession.CanUndo;
        PdfOrganizeRedoBtn.IsEnabled = _organizeSession.CanRedo;

        PdfOrganizeExportBtn.IsEnabled = _organizeSession.CanExport && !_isPdfOrganizing;

        // Keep preview index valid
        if (_pdfOrganizeCurrentPreviewIndex >= pageCount)
        {
            _pdfOrganizeCurrentPreviewIndex = Math.Max(0, pageCount - 1);
        }

        UpdatePreviewHeader();
    }

    private void UpdatePreviewHeader()
    {
        if (_organizeSession.Pages.Count == 0)
        {
            PdfOrganizePreviewPageIndicator.Text = "Page 0 of 0";
            PdfOrganizePreviewDimensionsText.Text = "Dimensions: —";
            PdfOrganizeLargePreviewImage.Source = null;
            return;
        }

        int pos = _pdfOrganizeCurrentPreviewIndex + 1;
        var curPage = _organizeSession.Pages[_pdfOrganizeCurrentPreviewIndex];
        PdfOrganizePreviewPageIndicator.Text = $"Page {pos} of {_organizeSession.Pages.Count} (Orig #{curPage.OriginalPageNumber})";

        double w = curPage.WidthPt;
        double h = curPage.HeightPt;
        int effRot = curPage.EffectiveRotation;
        PdfOrganizePreviewDimensionsText.Text = $"Dimensions: {w:F0} × {h:F0} pt · Effective Rotation: {effRot}°";

        PdfOrganizeLargePreviewRotateTransform.Angle = curPage.RotationDegrees;
    }

    private void UpdateLargePreview()
    {
        if (_organizeSession.Pages.Count == 0 || _pdfOrganizeCurrentPreviewIndex < 0 || _pdfOrganizeCurrentPreviewIndex >= _organizeSession.Pages.Count)
        {
            PdfOrganizeLargePreviewImage.Source = null;
            UpdatePreviewHeader();
            return;
        }

        var curPage = _organizeSession.Pages[_pdfOrganizeCurrentPreviewIndex];
        UpdatePreviewHeader();

        // Use cached thumbnail immediately for rapid responsiveness
        if (_thumbnailCache.TryGetValue(curPage.OriginalPageIndex, out var cachedImg))
        {
            PdfOrganizeLargePreviewImage.Source = cachedImg;
        }
    }

    // =========================================================================
    // TOOLBAR EVENT HANDLERS
    // =========================================================================

    private void PdfOrganizeSelectAll_Click(object sender, RoutedEventArgs e)
    {
        _organizeSession.SelectAll();
    }

    private void PdfOrganizeClearSelection_Click(object sender, RoutedEventArgs e)
    {
        _organizeSession.ClearSelection();
    }

    private void PdfOrganizeMoveLeft_Click(object sender, RoutedEventArgs e)
    {
        _organizeSession.MoveSelected(-1);
    }

    private void PdfOrganizeMoveRight_Click(object sender, RoutedEventArgs e)
    {
        _organizeSession.MoveSelected(1);
    }

    private void PdfOrganizeRotateCcw_Click(object sender, RoutedEventArgs e)
    {
        _organizeSession.RotateSelected(-90);
        UpdateLargePreview();
    }

    private void PdfOrganizeRotateCw_Click(object sender, RoutedEventArgs e)
    {
        _organizeSession.RotateSelected(90);
        UpdateLargePreview();
    }

    private void PdfOrganizeRotate180_Click(object sender, RoutedEventArgs e)
    {
        _organizeSession.RotateSelected(180);
        UpdateLargePreview();
    }

    private void PdfOrganizeDeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        int selCount = _organizeSession.SelectedCount;
        if (selCount > 1)
        {
            var res = MessageBox.Show(
                this,
                $"Are you sure you want to delete {selCount} selected pages from this document?",
                "Delete Pages Confirmation",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (res != MessageBoxResult.Yes) return;
        }

        _organizeSession.DeleteSelected();
        UpdateLargePreview();
    }

    private void PdfOrganizeUndo_Click(object sender, RoutedEventArgs e)
    {
        _organizeSession.Undo();
        UpdateLargePreview();
    }

    private void PdfOrganizeRedo_Click(object sender, RoutedEventArgs e)
    {
        _organizeSession.Redo();
        UpdateLargePreview();
    }

    private void PdfOrganizeReset_Click(object sender, RoutedEventArgs e)
    {
        var res = MessageBox.Show(
            this,
            "Reset all pages to original sequence, rotations, and restore deleted pages?",
            "Reset to Original",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (res == MessageBoxResult.Yes)
        {
            _organizeSession.ResetToOriginal();
            _pdfOrganizeCurrentPreviewIndex = 0;
            UpdateLargePreview();
        }
    }

    // =========================================================================
    // CARD & PREVIEW INTERACTION
    // =========================================================================

    private void PdfOrganizeCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is PdfOrganizeItemViewModel item)
        {
            int idx = _organizeSession.Pages.IndexOf(item);
            if (idx >= 0)
            {
                _pdfOrganizeCurrentPreviewIndex = idx;
                UpdateLargePreview();
            }
        }
    }

    private void PdfOrganizeItemCheckbox_Click(object sender, RoutedEventArgs e)
    {
        UpdateOrganizeUi();
    }

    private void PdfOrganizeItemMoveLeft_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is PdfOrganizeItemViewModel item)
        {
            int idx = _organizeSession.Pages.IndexOf(item);
            if (idx > 0)
            {
                _organizeSession.MovePage(idx, idx - 1);
                _pdfOrganizeCurrentPreviewIndex = idx - 1;
                UpdateLargePreview();
            }
        }
    }

    private void PdfOrganizeItemMoveRight_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is PdfOrganizeItemViewModel item)
        {
            int idx = _organizeSession.Pages.IndexOf(item);
            if (idx >= 0 && idx < _organizeSession.Pages.Count - 1)
            {
                _organizeSession.MovePage(idx, idx + 1);
                _pdfOrganizeCurrentPreviewIndex = idx + 1;
                UpdateLargePreview();
            }
        }
    }

    private void PdfOrganizeItemRotateCw_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is PdfOrganizeItemViewModel item)
        {
            int idx = _organizeSession.Pages.IndexOf(item);
            if (idx >= 0)
            {
                _organizeSession.RotatePage(idx, 90);
                UpdateLargePreview();
            }
        }
    }

    private void PdfOrganizeItemDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is PdfOrganizeItemViewModel item)
        {
            int idx = _organizeSession.Pages.IndexOf(item);
            if (idx >= 0)
            {
                _organizeSession.DeletePage(idx);
                UpdateLargePreview();
            }
        }
    }

    // =========================================================================
    // DRAG AND DROP REORDERING
    // =========================================================================

    private void PdfOrganizeCard_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is PdfOrganizeItemViewModel item)
        {
            _draggedCardItem = item;
            _cardDragStartPoint = e.GetPosition(null);
        }
    }

    private void PdfOrganizeCard_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && _draggedCardItem != null)
        {
            var currentPos = e.GetPosition(null);
            var diff = _cardDragStartPoint - currentPos;

            if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
            {
                var data = new DataObject("PdfOrganizeItem", _draggedCardItem);
                DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Move);
                _draggedCardItem = null;
            }
        }
    }

    private void PdfOrganizeCard_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent("PdfOrganizeItem"))
        {
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        }
    }

    private void PdfOrganizeCard_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent("PdfOrganizeItem") && sender is FrameworkElement fe && fe.DataContext is PdfOrganizeItemViewModel targetItem)
        {
            if (e.Data.GetData("PdfOrganizeItem") is PdfOrganizeItemViewModel sourceItem)
            {
                int sourceIdx = _organizeSession.Pages.IndexOf(sourceItem);
                int targetIdx = _organizeSession.Pages.IndexOf(targetItem);

                if (sourceIdx >= 0 && targetIdx >= 0 && sourceIdx != targetIdx)
                {
                    _organizeSession.MovePage(sourceIdx, targetIdx);
                    _pdfOrganizeCurrentPreviewIndex = targetIdx;
                    UpdateLargePreview();
                }
            }
            e.Handled = true;
        }
    }

    // =========================================================================
    // PREVIEW NAVIGATION & ZOOM
    // =========================================================================

    private void PdfOrganizePrevPage_Click(object sender, RoutedEventArgs e)
    {
        if (_pdfOrganizeCurrentPreviewIndex > 0)
        {
            _pdfOrganizeCurrentPreviewIndex--;
            UpdateLargePreview();
        }
    }

    private void PdfOrganizeNextPage_Click(object sender, RoutedEventArgs e)
    {
        if (_pdfOrganizeCurrentPreviewIndex < _organizeSession.Pages.Count - 1)
        {
            _pdfOrganizeCurrentPreviewIndex++;
            UpdateLargePreview();
        }
    }

    private void PdfOrganizeZoomIn_Click(object sender, RoutedEventArgs e)
    {
        _organizeZoomLevel = Math.Min(3.0, _organizeZoomLevel + 0.25);
        ApplyZoom();
    }

    private void PdfOrganizeZoomOut_Click(object sender, RoutedEventArgs e)
    {
        _organizeZoomLevel = Math.Max(0.5, _organizeZoomLevel - 0.25);
        ApplyZoom();
    }

    private void PdfOrganizeZoomFit_Click(object sender, RoutedEventArgs e)
    {
        _organizeZoomLevel = 1.0;
        ApplyZoom();
    }

    private void ApplyZoom()
    {
        if (PdfOrganizeLargePreviewImage != null)
        {
            PdfOrganizeLargePreviewImage.LayoutTransform = new ScaleTransform(_organizeZoomLevel, _organizeZoomLevel);
        }
    }

    // =========================================================================
    // EXPORT WORKFLOW
    // =========================================================================

    private void PdfOrganizeBrowseOutputDir_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select Destination Folder for Organized PDF",
            InitialDirectory = PdfOrganizeOutputDirBox.Text
        };

        if (dialog.ShowDialog(this) == true)
        {
            PdfOrganizeOutputDirBox.Text = dialog.FolderName;
        }
    }

    private async void PdfOrganizeExport_Click(object sender, RoutedEventArgs e)
    {
        if (!_organizeSession.CanExport)
        {
            MessageBox.Show(this, "Cannot export an empty PDF. At least one page must remain.", "Export Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string outDir = PdfOrganizeOutputDirBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(outDir))
        {
            outDir = App.Services.Outputs.RootDirectory;
            PdfOrganizeOutputDirBox.Text = outDir;
        }

        string outName = PdfOrganizeOutputFileNameBox.Text?.Trim() ?? string.Empty;

        _isPdfOrganizing = true;
        _pdfOrganizeCts = new CancellationTokenSource();
        var token = _pdfOrganizeCts.Token;

        PdfOrganizeExportBtn.IsEnabled = false;
        PdfOrganizeExportBtnText.Text = "Exporting PDF…";
        PdfOrganizeProgressBar.Visibility = Visibility.Visible;
        PdfOrganizeProgressBar.Value = 10;
        PdfOrganizeProgressText.Visibility = Visibility.Visible;
        PdfOrganizeProgressText.Text = "Preparing organized PDF…";
        PdfOrganizeSuccessBanner.Visibility = Visibility.Collapsed;

        var progress = new Progress<ProcessingProgress>(p =>
        {
            Dispatcher.Invoke(() =>
            {
                PdfOrganizeProgressBar.Value = p.Percent;
                PdfOrganizeProgressText.Text = p.Message;
            });
        });

        var request = new PdfOrganizeRequest(
            _organizeSession.SourceFilePath,
            outDir,
            _organizeSession.GetTransforms(),
            outName);

        var organizeStart = DateTimeOffset.UtcNow;
        var organizeSw = Stopwatch.StartNew();

        try
        {
            var result = await App.Services.PdfOrganize.OrganizeAsync(request, progress, token);
            organizeSw.Stop();

            var opStatus = result.WasCancelled
                ? OperationStatus.Cancelled
                : (result.Succeeded ? OperationStatus.Completed : OperationStatus.Failed);

            App.Services.HistoryStore.Record(new OperationRecord(
                Id: Guid.NewGuid().ToString("N"),
                ActionId: "pdf.organize",
                ActionName: "Organize PDF Pages",
                Category: OperationCategory.Pdf,
                InputPaths: [_organizeSession.SourceFilePath],
                OutputPaths: result.OutputPath is not null ? [result.OutputPath] : [],
                StartedAtUtc: organizeStart,
                CompletedAtUtc: DateTimeOffset.UtcNow,
                Duration: organizeSw.Elapsed,
                Status: opStatus,
                Options: new Dictionary<string, string> { ["PageCount"] = result.OutputPageCount.ToString() },
                PresetName: null,
                FailureReason: result.ErrorReason,
                RetryOfOperationId: null));

            if (result.WasCancelled)
            {
                PdfOrganizeProgressText.Text = "Export cancelled.";
                return;
            }

            if (!result.Succeeded)
            {
                MessageBox.Show(this, result.ErrorReason ?? "PDF export failed.", "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
                PdfOrganizeProgressText.Text = $"Export failed: {result.ErrorReason}";
                return;
            }

            _lastOrganizedPdfPath = result.OutputPath;
            PdfOrganizeSuccessMessage.Text = $"Exported {result.OutputPageCount} page(s) successfully to '{Path.GetFileName(result.OutputPath)}'.";
            PdfOrganizeSuccessBanner.Visibility = Visibility.Visible;
            PdfOrganizeProgressText.Text = "Export complete!";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Export failed: {ex.Message}", "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
            PdfOrganizeProgressText.Text = $"Export error: {ex.Message}";
        }
        finally
        {
            _isPdfOrganizing = false;
            PdfOrganizeExportBtn.IsEnabled = true;
            PdfOrganizeExportBtnText.Text = "Export Organized PDF";
            PdfOrganizeProgressBar.Visibility = Visibility.Collapsed;
        }
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes >= 1024 * 1024)
            return $"{bytes / (1024.0 * 1024.0):F1} MB";
        if (bytes >= 1024)
            return $"{bytes / 1024.0:F1} KB";
        return $"{bytes} B";
    }

    private void PdfOrganizeRevealOutput_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_lastOrganizedPdfPath) && File.Exists(_lastOrganizedPdfPath))
        {
            ShellFolders.RevealInExplorer(_lastOrganizedPdfPath);
        }
    }

    private void PdfOrganizeOpenOutput_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_lastOrganizedPdfPath) && File.Exists(_lastOrganizedPdfPath))
        {
            ShellFolders.OpenFile(_lastOrganizedPdfPath);
        }
    }
}
