using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using SmartShift.App.Ui;
using SmartShift.Core.Classification;
using SmartShift.Core.History;
using SmartShift.Core.IO;
using SmartShift.Processing;
using SmartShift.Processing.Pdf;
using SmartShift.Processing.Pdf.Preview;
using SmartShift.Windows.Shell;

namespace SmartShift.App;

public partial class MainWindow : Window
{
    private string? _pdfSplitSourcePath;
    private int _pdfSplitCurrentPage = 1;
    private int _pdfSplitTotalPages = 0;
    private readonly HashSet<int> _pdfSplitSelectedPages = [];
    private readonly ObservableCollection<PdfSplitPlannedOutputViewModel> _pdfSplitPlannedOutputs = [];
    private CancellationTokenSource? _pdfSplitCts;
    private bool _isPdfSplitting = false;
    private bool _isUpdatingRangeText = false;
    private bool _isUpdatingSelectionCheck = false;
    private string? _lastSplitOutputFolder;
    private string? _lastSplitFirstOutputFile;

    private void InitializePdfSplitStudio()
    {
        if (PdfSplitPlannedOutputsList is not null)
        {
            PdfSplitPlannedOutputsList.ItemsSource = _pdfSplitPlannedOutputs;
        }

        if (PdfSplitOutputDirBox is not null && string.IsNullOrWhiteSpace(PdfSplitOutputDirBox.Text))
        {
            PdfSplitOutputDirBox.Text = App.Services.Outputs.RootDirectory;
        }
    }

    public async void OpenPdfSplitStudio(string? targetPdfPath = null)
    {
        if (ImageEditorHost.Visibility == Visibility.Visible) CloseImageEditor();
        if (BatchStudioHost.Visibility == Visibility.Visible) CloseBatchStudio();
        if (DocumentBuilderHost.Visibility == Visibility.Visible) CloseDocumentBuilder();
        if (PresentationStudioHost.Visibility == Visibility.Visible) ClosePresentationStudio();
        if (PdfMergeStudioHost.Visibility == Visibility.Visible) ClosePdfMergeStudio();
        if (PdfOrganizeStudioHost.Visibility == Visibility.Visible) ClosePdfOrganizeStudio();
        ClosePdfPreview();

        PdfSplitStudioHost.Visibility = Visibility.Visible;
        NavPdfSplitBtn.Tag = "Active";
        NavWorkspaceBtn.Tag = null;
        NavQueueBtn.Tag = null;
        NavDocBuilderBtn.Tag = null;
        NavPresentationBtn.Tag = null;
        NavPdfMergeBtn.Tag = null;
        NavPdfOrganizeBtn.Tag = null;

        // Determine path to load
        string? pathToLoad = targetPdfPath;
        if (string.IsNullOrWhiteSpace(pathToLoad) && _selectedFileItem is not null && _selectedFileItem.File.Format == DetectedFormat.Pdf)
        {
            pathToLoad = _selectedFileItem.File.Path;
        }

        if (!string.IsNullOrWhiteSpace(pathToLoad) && File.Exists(pathToLoad))
        {
            await LoadPdfSplitDocumentAsync(pathToLoad);
        }
        else
        {
            ShowEmptyPdfSplitState("Select or open a PDF document to begin splitting.");
        }
    }

    public void ClosePdfSplitStudio()
    {
        if (_isPdfSplitting && _pdfSplitCts is not null)
        {
            _pdfSplitCts.Cancel();
        }

        PdfSplitStudioHost.Visibility = Visibility.Collapsed;
        NavPdfSplitBtn.Tag = null;
    }

    private void NavPdfSplit_Click(object sender, RoutedEventArgs e)
    {
        if (PdfSplitStudioHost.Visibility == Visibility.Visible)
        {
            ClosePdfSplitStudio();
        }
        else
        {
            OpenPdfSplitStudio();
        }
    }

    private void ClosePdfSplitStudio_Click(object sender, RoutedEventArgs e)
    {
        ClosePdfSplitStudio();
    }

    private void ShowEmptyPdfSplitState(string message)
    {
        _pdfSplitSourcePath = null;
        _pdfSplitCurrentPage = 1;
        _pdfSplitTotalPages = 0;
        _pdfSplitSelectedPages.Clear();
        _pdfSplitPlannedOutputs.Clear();

        PdfSplitHeaderFileName.Text = "No Document Selected";
        PdfSplitHeaderFileSize.Text = "—";
        PdfSplitHeaderPageCount.Text = "—";
        PdfSplitHeaderStatus.Text = message;

        PdfSplitPreviewImage.Source = null;
        PdfSplitPreviewEmptyText.Text = message;
        PdfSplitPreviewEmptyText.Visibility = Visibility.Visible;
        PdfSplitPageCounter.Text = "Page 0 of 0";
        PdfSplitSelectionCountText.Text = "0 pages selected";
        PdfSplitPageSelectCheck.IsEnabled = false;
        PdfSplitPageSelectCheck.IsChecked = false;
        PdfSplitExecuteBtn.IsEnabled = false;
        PdfSplitRangeBox.IsEnabled = false;
        PdfSplitModeExtractRadio.IsEnabled = false;
        PdfSplitModeEveryRadio.IsEnabled = false;
    }

    public async Task LoadPdfSplitDocumentAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            ShowEmptyPdfSplitState("Selected PDF file could not be found.");
            return;
        }

        var validation = await App.Services.PdfSplit.ValidateSourceAsync(filePath);
        if (!validation.IsValid)
        {
            ShowEmptyPdfSplitState(validation.ErrorReason ?? "The selected file is not a valid PDF.");
            return;
        }

        _pdfSplitSourcePath = filePath;
        _pdfSplitTotalPages = validation.TotalPages;
        _pdfSplitCurrentPage = 1;
        _pdfSplitSelectedPages.Clear();

        PdfSplitHeaderFileName.Text = Path.GetFileName(filePath);
        PdfSplitHeaderFileSize.Text = FileSizeFormatter.Format(validation.FileSizeBytes);
        PdfSplitHeaderPageCount.Text = $"{validation.TotalPages} page{(validation.TotalPages > 1 ? "s" : "")}";
        PdfSplitHeaderStatus.Text = "Document loaded and ready";

        PdfSplitOutputDirBox.Text = App.Services.Outputs.RootDirectory;
        PdfSplitResultCard.Visibility = Visibility.Collapsed;
        PdfSplitProgressHost.Visibility = Visibility.Collapsed;

        PdfSplitPageSelectCheck.IsEnabled = true;
        PdfSplitRangeBox.IsEnabled = true;
        PdfSplitModeExtractRadio.IsEnabled = true;
        PdfSplitModeEveryRadio.IsEnabled = true;

        // Default: select all pages
        for (int p = 1; p <= _pdfSplitTotalPages; p++)
        {
            _pdfSplitSelectedPages.Add(p);
        }

        _isUpdatingRangeText = true;
        PdfSplitRangeBox.Text = _pdfSplitTotalPages > 1 ? $"1-{_pdfSplitTotalPages}" : "1";
        _isUpdatingRangeText = false;

        UpdateSelectionBadge();
        RefreshPlannedOutputs();
        await LoadPdfSplitPagePreviewAsync(0);
    }

    private async Task LoadPdfSplitPagePreviewAsync(int pageIndex)
    {
        if (string.IsNullOrWhiteSpace(_pdfSplitSourcePath) || !File.Exists(_pdfSplitSourcePath) || _pdfSplitTotalPages <= 0)
        {
            PdfSplitPreviewImage.Source = null;
            PdfSplitPreviewEmptyText.Visibility = Visibility.Visible;
            return;
        }

        int safePage = Math.Clamp(pageIndex, 0, _pdfSplitTotalPages - 1);
        _pdfSplitCurrentPage = safePage + 1;
        PdfSplitPageCounter.Text = $"Page {_pdfSplitCurrentPage} of {_pdfSplitTotalPages}";

        _isUpdatingSelectionCheck = true;
        PdfSplitPageSelectCheck.IsChecked = _pdfSplitSelectedPages.Contains(_pdfSplitCurrentPage);
        PdfSplitPageSelectCheck.Content = $"Include page {_pdfSplitCurrentPage} in selection";
        _isUpdatingSelectionCheck = false;

        try
        {
            var pageData = await PdfPreviewRenderer.RenderPageAsync(_pdfSplitSourcePath, safePage, 96.0);
            if (pageData is not null)
            {
                var bitmap = new WriteableBitmap(
                    pageData.PixelWidth,
                    pageData.PixelHeight,
                    96,
                    96,
                    PixelFormats.Bgra32,
                    null);

                bitmap.WritePixels(
                    new Int32Rect(0, 0, pageData.PixelWidth, pageData.PixelHeight),
                    pageData.BgraBytes,
                    pageData.Stride,
                    0);

                PdfSplitPreviewImage.Source = bitmap;
                PdfSplitPreviewEmptyText.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception ex)
        {
            PdfSplitPreviewImage.Source = null;
            PdfSplitPreviewEmptyText.Text = $"Preview failed: {ex.Message}";
            PdfSplitPreviewEmptyText.Visibility = Visibility.Visible;
        }
    }

    private async void PdfSplitPrevPage_Click(object sender, RoutedEventArgs e)
    {
        if (_pdfSplitTotalPages > 0 && _pdfSplitCurrentPage > 1)
        {
            await LoadPdfSplitPagePreviewAsync(_pdfSplitCurrentPage - 2);
        }
    }

    private async void PdfSplitNextPage_Click(object sender, RoutedEventArgs e)
    {
        if (_pdfSplitTotalPages > 0 && _pdfSplitCurrentPage < _pdfSplitTotalPages)
        {
            await LoadPdfSplitPagePreviewAsync(_pdfSplitCurrentPage);
        }
    }

    private void PdfSplitPageSelectCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingSelectionCheck || _pdfSplitTotalPages <= 0) return;

        bool isChecked = PdfSplitPageSelectCheck.IsChecked == true;
        if (isChecked)
        {
            _pdfSplitSelectedPages.Add(_pdfSplitCurrentPage);
        }
        else
        {
            _pdfSplitSelectedPages.Remove(_pdfSplitCurrentPage);
        }

        _isUpdatingRangeText = true;
        PdfSplitRangeBox.Text = PdfPageRangeParser.FormatSelectedPages(_pdfSplitSelectedPages);
        _isUpdatingRangeText = false;

        UpdateSelectionBadge();
        RefreshPlannedOutputs();
    }

    private void PdfSplitSelectAll_Click(object sender, RoutedEventArgs e)
    {
        if (_pdfSplitTotalPages <= 0) return;

        _pdfSplitSelectedPages.Clear();
        for (int p = 1; p <= _pdfSplitTotalPages; p++)
        {
            _pdfSplitSelectedPages.Add(p);
        }

        _isUpdatingSelectionCheck = true;
        PdfSplitPageSelectCheck.IsChecked = true;
        _isUpdatingSelectionCheck = false;

        _isUpdatingRangeText = true;
        PdfSplitRangeBox.Text = _pdfSplitTotalPages > 1 ? $"1-{_pdfSplitTotalPages}" : "1";
        _isUpdatingRangeText = false;

        UpdateSelectionBadge();
        RefreshPlannedOutputs();
    }

    private void PdfSplitClearSelection_Click(object sender, RoutedEventArgs e)
    {
        if (_pdfSplitTotalPages <= 0) return;

        _pdfSplitSelectedPages.Clear();

        _isUpdatingSelectionCheck = true;
        PdfSplitPageSelectCheck.IsChecked = false;
        _isUpdatingSelectionCheck = false;

        _isUpdatingRangeText = true;
        PdfSplitRangeBox.Text = string.Empty;
        _isUpdatingRangeText = false;

        UpdateSelectionBadge();
        RefreshPlannedOutputs();
    }

    private void PdfSplitRangeBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isUpdatingRangeText || _pdfSplitTotalPages <= 0) return;

        RefreshPlannedOutputs();
    }

    private void UpdateSelectionBadge()
    {
        PdfSplitSelectionCountText.Text = $"{_pdfSplitSelectedPages.Count} of {_pdfSplitTotalPages} pages selected";
    }

    private void PdfSplitMode_Checked(object sender, RoutedEventArgs e)
    {
        if (PdfSplitModeExtractPanel is null || PdfSplitModeEveryPanel is null) return;

        bool isExtract = PdfSplitModeExtractRadio.IsChecked == true;
        PdfSplitModeExtractPanel.Visibility = isExtract ? Visibility.Visible : Visibility.Collapsed;
        PdfSplitModeEveryPanel.Visibility = isExtract ? Visibility.Collapsed : Visibility.Visible;

        if (PdfSplitExecuteBtn is not null)
        {
            PdfSplitExecuteBtnText.Text = isExtract ? "Extract Selected Ranges" : "Split Every Page";
        }

        RefreshPlannedOutputs();
    }

    private void RefreshPlannedOutputs()
    {
        _pdfSplitPlannedOutputs.Clear();

        if (string.IsNullOrWhiteSpace(_pdfSplitSourcePath) || _pdfSplitTotalPages <= 0)
        {
            PdfSplitExecuteBtn.IsEnabled = false;
            PdfSplitRangeValidationText.Visibility = Visibility.Collapsed;
            return;
        }

        var sourceBaseName = Path.GetFileNameWithoutExtension(_pdfSplitSourcePath);
        int padDigits = Math.Max(3, _pdfSplitTotalPages.ToString().Length);

        if (PdfSplitModeEveryRadio.IsChecked == true)
        {
            PdfSplitRangeValidationText.Visibility = Visibility.Collapsed;
            PdfSplitExecuteBtn.IsEnabled = true;

            int previewLimit = Math.Min(_pdfSplitTotalPages, 10);
            for (int p = 1; p <= previewLimit; p++)
            {
                string suffix = $"_page_{p.ToString($"D{padDigits}")}.pdf";
                _pdfSplitPlannedOutputs.Add(new PdfSplitPlannedOutputViewModel(
                    $"{sourceBaseName}{suffix}",
                    $"Page {p}",
                    1));
            }

            if (_pdfSplitTotalPages > previewLimit)
            {
                _pdfSplitPlannedOutputs.Add(new PdfSplitPlannedOutputViewModel(
                    $"… and {_pdfSplitTotalPages - previewLimit} more file(s)",
                    $"Pages {previewLimit + 1}–{_pdfSplitTotalPages}",
                    _pdfSplitTotalPages - previewLimit));
            }

            PdfSplitEveryTotalNote.Text = $"Will generate {_pdfSplitTotalPages} individual 1-page PDF file(s).";
            return;
        }

        // Mode A: Extract Ranges
        string rangeText = PdfSplitRangeBox?.Text ?? string.Empty;
        if (!PdfPageRangeParser.TryParseRanges(rangeText, _pdfSplitTotalPages, out var ranges, out var error))
        {
            PdfSplitRangeValidationText.Text = error ?? "Invalid page range.";
            PdfSplitRangeValidationText.Visibility = Visibility.Visible;
            PdfSplitExecuteBtn.IsEnabled = false;

            if (!_isUpdatingRangeText)
            {
                _pdfSplitSelectedPages.Clear();
                _isUpdatingSelectionCheck = true;
                PdfSplitPageSelectCheck.IsChecked = false;
                _isUpdatingSelectionCheck = false;
                UpdateSelectionBadge();
            }
            return;
        }

        PdfSplitRangeValidationText.Visibility = Visibility.Collapsed;
        PdfSplitExecuteBtn.IsEnabled = ranges.Count > 0 && !_isPdfSplitting;

        if (!_isUpdatingRangeText)
        {
            _pdfSplitSelectedPages.Clear();
            foreach (var r in ranges)
            {
                foreach (int p in r.Pages)
                {
                    _pdfSplitSelectedPages.Add(p);
                }
            }

            _isUpdatingSelectionCheck = true;
            PdfSplitPageSelectCheck.IsChecked = _pdfSplitSelectedPages.Contains(_pdfSplitCurrentPage);
            _isUpdatingSelectionCheck = false;
            UpdateSelectionBadge();
        }

        foreach (var r in ranges)
        {
            string suffix = $"{r.ToSuggestedFileSuffix(padDigits)}.pdf";
            _pdfSplitPlannedOutputs.Add(new PdfSplitPlannedOutputViewModel(
                $"{sourceBaseName}{suffix}",
                r.IsSinglePage ? $"Page {r.StartPage}" : $"Pages {r.StartPage}–{r.EndPage}",
                r.PageCount));
        }
    }

    private void PdfSplitBrowseDir_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select PDF Split Output Directory",
            InitialDirectory = Directory.Exists(PdfSplitOutputDirBox.Text)
                ? PdfSplitOutputDirBox.Text
                : App.Services.Outputs.RootDirectory
        };

        if (dialog.ShowDialog(this) == true)
        {
            PdfSplitOutputDirBox.Text = dialog.FolderName;
        }
    }

    private void PdfSplitCancel_Click(object sender, RoutedEventArgs e)
    {
        if (_isPdfSplitting && _pdfSplitCts is not null)
        {
            _pdfSplitCts.Cancel();
            PdfSplitProgressText.Text = "Cancelling and cleaning up…";
            PdfSplitCancelBtn.IsEnabled = false;
        }
    }

    private async void PdfSplitExecute_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_pdfSplitSourcePath) || !File.Exists(_pdfSplitSourcePath) || _isPdfSplitting)
        {
            return;
        }

        var mode = PdfSplitModeEveryRadio.IsChecked == true ? PdfSplitMode.SplitEveryPage : PdfSplitMode.ExtractRanges;
        IReadOnlyList<PdfPageRangeItem>? ranges = null;

        if (mode == PdfSplitMode.ExtractRanges)
        {
            if (!PdfPageRangeParser.TryParseRanges(PdfSplitRangeBox.Text, _pdfSplitTotalPages, out ranges, out var error))
            {
                PdfSplitRangeValidationText.Text = error ?? "Invalid range.";
                PdfSplitRangeValidationText.Visibility = Visibility.Visible;
                return;
            }
        }

        var outDir = string.IsNullOrWhiteSpace(PdfSplitOutputDirBox.Text)
            ? App.Services.Outputs.RootDirectory
            : PdfSplitOutputDirBox.Text.Trim();

        _pdfSplitCts?.Dispose();
        _pdfSplitCts = new CancellationTokenSource();
        var token = _pdfSplitCts.Token;

        _isPdfSplitting = true;
        PdfSplitExecuteBtn.IsEnabled = false;
        PdfSplitCancelBtn.IsEnabled = true;
        PdfSplitProgressHost.Visibility = Visibility.Visible;
        PdfSplitProgressBar.IsIndeterminate = true;
        PdfSplitProgressBar.Value = 0;
        PdfSplitProgressText.Text = "Preparing to split PDF…";
        PdfSplitResultCard.Visibility = Visibility.Collapsed;

        var progress = new Progress<ProcessingProgress>(p =>
        {
            Dispatcher.InvokeAsync(() =>
            {
                PdfSplitProgressBar.IsIndeterminate = false;
                PdfSplitProgressBar.Value = p.Percent;
                PdfSplitProgressText.Text = $"{p.Percent}% — {p.Message}";
            });
        });

        var splitStart = DateTimeOffset.UtcNow;
        var splitSw = Stopwatch.StartNew();

        try
        {
            var splitRequest = new PdfSplitRequest(_pdfSplitSourcePath, outDir, mode, ranges);
            var result = await App.Services.PdfSplit.SplitAsync(splitRequest, progress, token);
            splitSw.Stop();

            _lastSplitOutputFolder = result.OutputDirectory;
            _lastSplitFirstOutputFile = result.GeneratedFiles.Count > 0 ? result.GeneratedFiles[0].OutputPath : null;

            var opStatus = result.WasCancelled
                ? OperationStatus.Cancelled
                : (result.Succeeded ? OperationStatus.Completed : OperationStatus.Failed);

            App.Services.HistoryStore.Record(new OperationRecord(
                Id: Guid.NewGuid().ToString("N"),
                ActionId: "pdf.split",
                ActionName: "Split PDF",
                Category: OperationCategory.Pdf,
                InputPaths: [_pdfSplitSourcePath],
                OutputPaths: result.GeneratedFiles.Select(g => g.OutputPath).ToList(),
                StartedAtUtc: splitStart,
                CompletedAtUtc: DateTimeOffset.UtcNow,
                Duration: splitSw.Elapsed,
                Status: opStatus,
                Options: new Dictionary<string, string> { ["Mode"] = mode.ToString() },
                PresetName: null,
                FailureReason: result.ErrorReason,
                RetryOfOperationId: null));

            if (result.WasCancelled)
            {
                PdfSplitResultCard.Background = (Brush)FindResource("AmberBgBrush");
                PdfSplitResultCard.BorderBrush = (Brush)FindResource("AmberBrush");
                PdfSplitResultTitle.Text = "Split Operation Cancelled";
                PdfSplitResultTitle.Foreground = (Brush)FindResource("AmberBrush");
                PdfSplitResultDetail.Text = "Operation cancelled. Incomplete temporary files were cleaned up.";
                PdfSplitResultCard.Visibility = Visibility.Visible;
                PdfSplitOpenFirstBtn.Visibility = Visibility.Collapsed;
            }
            else if (!result.Succeeded)
            {
                PdfSplitResultCard.Background = (Brush)FindResource("DangerBgBrush");
                PdfSplitResultCard.BorderBrush = (Brush)FindResource("DangerBrush");
                PdfSplitResultTitle.Text = "Split Operation Failed";
                PdfSplitResultTitle.Foreground = (Brush)FindResource("DangerBrush");
                PdfSplitResultDetail.Text = result.ErrorReason ?? "An error occurred during processing.";
                PdfSplitResultCard.Visibility = Visibility.Visible;
                PdfSplitOpenFirstBtn.Visibility = Visibility.Collapsed;
            }
            else
            {
                PdfSplitResultCard.Background = (Brush)FindResource("OkBgBrush");
                PdfSplitResultCard.BorderBrush = (Brush)FindResource("OkBrush");
                PdfSplitResultTitle.Text = "PDF Split Completed Successfully";
                PdfSplitResultTitle.Foreground = (Brush)FindResource("OkBrush");
                PdfSplitResultDetail.Text = result.SummaryMessage;
                PdfSplitResultCard.Visibility = Visibility.Visible;
                PdfSplitOpenFirstBtn.Visibility = File.Exists(_lastSplitFirstOutputFile ?? "") ? Visibility.Visible : Visibility.Collapsed;
            }
        }
        catch (OperationCanceledException)
        {
            PdfSplitResultCard.Background = (Brush)FindResource("AmberBgBrush");
            PdfSplitResultCard.BorderBrush = (Brush)FindResource("AmberBrush");
            PdfSplitResultTitle.Text = "Split Operation Cancelled";
            PdfSplitResultTitle.Foreground = (Brush)FindResource("AmberBrush");
            PdfSplitResultDetail.Text = "Operation cancelled. Source file was not modified.";
            PdfSplitResultCard.Visibility = Visibility.Visible;
            PdfSplitOpenFirstBtn.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            PdfSplitResultCard.Background = (Brush)FindResource("DangerBgBrush");
            PdfSplitResultCard.BorderBrush = (Brush)FindResource("DangerBrush");
            PdfSplitResultTitle.Text = "Split Operation Failed";
            PdfSplitResultTitle.Foreground = (Brush)FindResource("DangerBrush");
            PdfSplitResultDetail.Text = ex.Message;
            PdfSplitResultCard.Visibility = Visibility.Visible;
            PdfSplitOpenFirstBtn.Visibility = Visibility.Collapsed;
        }
        finally
        {
            _isPdfSplitting = false;
            PdfSplitExecuteBtn.IsEnabled = true;
            PdfSplitProgressHost.Visibility = Visibility.Collapsed;
        }
    }

    private void PdfSplitOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = _lastSplitOutputFolder ?? PdfSplitOutputDirBox.Text;
        if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
        {
            ShellFolders.OpenDirectory(folder);
        }
    }

    private void PdfSplitOpenFirst_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_lastSplitFirstOutputFile) && File.Exists(_lastSplitFirstOutputFile))
        {
            ShellFolders.OpenFile(_lastSplitFirstOutputFile);
        }
    }
}
