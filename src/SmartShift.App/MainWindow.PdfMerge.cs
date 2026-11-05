using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using SmartShift.App.Ui;
using SmartShift.Core.History;
using SmartShift.Core.IO;
using SmartShift.Processing;
using SmartShift.Processing.Pdf;
using SmartShift.Processing.Pdf.Preview;
using SmartShift.Windows.DragDrop;
using SmartShift.Windows.Shell;

namespace SmartShift.App;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<PdfMergeItemViewModel> _pdfMergeItems = [];
    private PdfMergeItemViewModel? _pdfMergeSelectedItem;
    private CancellationTokenSource? _pdfMergeCts;
    private bool _isPdfMerging = false;
    private int _pdfMergePreviewPageIndex = 0;
    private int _pdfMergePreviewTotalPages = 0;
    private string? _lastMergedPdfPath;

    private void InitializePdfMergeStudio()
    {
        PdfMergeQueueList.ItemsSource = _pdfMergeItems;
    }

    public void OpenPdfMergeStudio(string? initialPdfPath = null)
    {
        if (ImageEditorHost.Visibility == Visibility.Visible) CloseImageEditor();
        if (BatchStudioHost.Visibility == Visibility.Visible) CloseBatchStudio();
        if (DocumentBuilderHost.Visibility == Visibility.Visible) CloseDocumentBuilder();
        if (PresentationStudioHost.Visibility == Visibility.Visible) ClosePresentationStudio();
        if (PdfSplitStudioHost.Visibility == Visibility.Visible) ClosePdfSplitStudio();
        if (PdfOrganizeStudioHost.Visibility == Visibility.Visible) ClosePdfOrganizeStudio();
        ClosePdfPreview();

        PdfMergeStudioHost.Visibility = Visibility.Visible;
        NavPdfMergeBtn.Tag = "Active";
        NavPdfSplitBtn.Tag = null;
        NavPdfOrganizeBtn.Tag = null;

        if (!string.IsNullOrWhiteSpace(initialPdfPath) && File.Exists(initialPdfPath))
        {
            AddFilesToPdfMergeQueue([initialPdfPath]);
        }
        else
        {
            UpdatePdfMergeQueueUi();
        }
    }

    public void ClosePdfMergeStudio()
    {
        PdfMergeStudioHost.Visibility = Visibility.Collapsed;
        NavPdfMergeBtn.Tag = null;
    }

    private void NavPdfMerge_Click(object sender, RoutedEventArgs e)
    {
        if (PdfMergeStudioHost.Visibility == Visibility.Visible)
        {
            ClosePdfMergeStudio();
        }
        else
        {
            OpenPdfMergeStudio();
        }
    }

    private void ClosePdfMergeStudio_Click(object sender, RoutedEventArgs e)
    {
        ClosePdfMergeStudio();
    }

    private void PdfMergeAddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select PDF Documents to Merge",
            Filter = "PDF Documents (*.pdf)|*.pdf|All Files (*.*)|*.*",
            Multiselect = true
        };

        if (dialog.ShowDialog(this) == true)
        {
            AddFilesToPdfMergeQueue(dialog.FileNames);
        }
    }

    public void AddFilesToPdfMergeQueue(IEnumerable<string> paths)
    {
        var validExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".pdf" };
        var addedAny = false;

        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
            var ext = Path.GetExtension(path);
            if (!validExtensions.Contains(ext)) continue;

            var fullPath = Path.GetFullPath(path);
            var itemValidation = new PdfMergeItemValidation(
                fullPath,
                Path.GetFileName(fullPath),
                new FileInfo(fullPath).Length,
                0,
                IsValid: true,
                IsEncrypted: false,
                ErrorReason: null);

            var vm = new PdfMergeItemViewModel(itemValidation, _pdfMergeItems.Count + 1);
            _pdfMergeItems.Add(vm);
            addedAny = true;
        }

        if (addedAny)
        {
            _ = RefreshPdfMergeQueueValidationAsync();
        }
        else
        {
            UpdatePdfMergeQueueUi();
        }
    }

    private async Task RefreshPdfMergeQueueValidationAsync()
    {
        var paths = _pdfMergeItems.Select(i => i.FilePath).ToList();
        var queueValidation = await App.Services.PdfMerge.ValidateQueueAsync(paths).ConfigureAwait(true);

        for (int i = 0; i < queueValidation.Items.Count && i < _pdfMergeItems.Count; i++)
        {
            _pdfMergeItems[i].UpdateValidation(queueValidation.Items[i]);
            _pdfMergeItems[i].PositionIndex = i + 1;
        }

        UpdatePdfMergeQueueUi();

        if (_pdfMergeSelectedItem is null && _pdfMergeItems.Count > 0)
        {
            PdfMergeQueueList.SelectedIndex = 0;
        }
    }

    private void UpdatePdfMergeQueueUi()
    {
        for (int i = 0; i < _pdfMergeItems.Count; i++)
        {
            _pdfMergeItems[i].PositionIndex = i + 1;
            _pdfMergeItems[i].CanMoveUp = i > 0;
            _pdfMergeItems[i].CanMoveDown = i < _pdfMergeItems.Count - 1;
        }

        int totalFiles = _pdfMergeItems.Count;
        int totalPages = _pdfMergeItems.Where(i => i.IsValid).Sum(i => i.PageCount);
        bool hasInvalid = _pdfMergeItems.Any(i => !i.IsValid);

        PdfMergeSummaryText.Text = $"{totalFiles} file{(totalFiles == 1 ? "" : "s")} · {totalPages} page{(totalPages == 1 ? "" : "s")}";
        PdfMergeEmptyPlaceholder.Visibility = totalFiles == 0 ? Visibility.Visible : Visibility.Collapsed;
        PdfMergeExecuteBtn.IsEnabled = totalFiles >= 2 && !hasInvalid && !_isPdfMerging;

        bool hasDuplicates = _pdfMergeItems.Any(i => i.IsDuplicate);
        PdfMergeRemoveDupBtn.Visibility = hasDuplicates ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PdfMergeClearQueue_Click(object sender, RoutedEventArgs e)
    {
        _pdfMergeItems.Clear();
        _pdfMergeSelectedItem = null;
        PdfMergePreviewImage.Source = null;
        PdfMergePreviewEmptyText.Visibility = Visibility.Visible;
        PdfMergePreviewTitle.Text = "Select a document to inspect";
        PdfMergePageCounter.Text = "Page 1 of 1";
        UpdatePdfMergeQueueUi();
    }

    private async void PdfMergeRemoveDuplicates_Click(object sender, RoutedEventArgs e)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = _pdfMergeItems.Count - 1; i >= 0; i--)
        {
            if (!seen.Add(_pdfMergeItems[i].FilePath))
            {
                _pdfMergeItems.RemoveAt(i);
            }
        }

        await RefreshPdfMergeQueueValidationAsync();
    }

    private async void PdfMergeRemoveItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: PdfMergeItemViewModel vm })
        {
            _pdfMergeItems.Remove(vm);
            if (_pdfMergeSelectedItem == vm)
            {
                _pdfMergeSelectedItem = _pdfMergeItems.FirstOrDefault();
                if (_pdfMergeSelectedItem is not null)
                {
                    PdfMergeQueueList.SelectedItem = _pdfMergeSelectedItem;
                }
            }
            await RefreshPdfMergeQueueValidationAsync();
        }
    }

    private async void PdfMergeMoveUp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: PdfMergeItemViewModel vm })
        {
            int index = _pdfMergeItems.IndexOf(vm);
            if (index > 0)
            {
                _pdfMergeItems.Move(index, index - 1);
                await RefreshPdfMergeQueueValidationAsync();
            }
        }
    }

    private async void PdfMergeMoveDown_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: PdfMergeItemViewModel vm })
        {
            int index = _pdfMergeItems.IndexOf(vm);
            if (index >= 0 && index < _pdfMergeItems.Count - 1)
            {
                _pdfMergeItems.Move(index, index + 1);
                await RefreshPdfMergeQueueValidationAsync();
            }
        }
    }

    private async void PdfMergeQueueList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PdfMergeQueueList.SelectedItem is PdfMergeItemViewModel vm)
        {
            _pdfMergeSelectedItem = vm;
            _pdfMergePreviewPageIndex = 0;
            await LoadPdfMergePreviewAsync(vm.FilePath, 0);
        }
    }

    private async Task LoadPdfMergePreviewAsync(string filePath, int pageIndex)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            PdfMergePreviewImage.Source = null;
            PdfMergePreviewEmptyText.Visibility = Visibility.Visible;
            PdfMergePreviewTitle.Text = "Document unavailable";
            return;
        }

        PdfMergePreviewTitle.Text = Path.GetFileName(filePath);

        try
        {
            var info = await PdfPreviewRenderer.GetDocumentInfoAsync(filePath);
            _pdfMergePreviewTotalPages = info.TotalPages;

            if (info.TotalPages <= 0)
            {
                PdfMergePreviewImage.Source = null;
                PdfMergePreviewEmptyText.Text = info.ErrorMessage ?? "Cannot preview document.";
                PdfMergePreviewEmptyText.Visibility = Visibility.Visible;
                PdfMergePageCounter.Text = "—";
                return;
            }

            int safePage = Math.Clamp(pageIndex, 0, info.TotalPages - 1);
            _pdfMergePreviewPageIndex = safePage;
            PdfMergePageCounter.Text = $"Page {safePage + 1} of {info.TotalPages}";

            var pageData = await PdfPreviewRenderer.RenderPageAsync(filePath, safePage, 96.0);
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

                PdfMergePreviewImage.Source = bitmap;
                PdfMergePreviewEmptyText.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception ex)
        {
            PdfMergePreviewImage.Source = null;
            PdfMergePreviewEmptyText.Text = $"Preview failed: {ex.Message}";
            PdfMergePreviewEmptyText.Visibility = Visibility.Visible;
        }
    }

    private async void PdfMergePrevPage_Click(object sender, RoutedEventArgs e)
    {
        if (_pdfMergeSelectedItem is not null && _pdfMergePreviewPageIndex > 0)
        {
            _pdfMergePreviewPageIndex--;
            await LoadPdfMergePreviewAsync(_pdfMergeSelectedItem.FilePath, _pdfMergePreviewPageIndex);
        }
    }

    private async void PdfMergeNextPage_Click(object sender, RoutedEventArgs e)
    {
        if (_pdfMergeSelectedItem is not null && _pdfMergePreviewPageIndex < _pdfMergePreviewTotalPages - 1)
        {
            _pdfMergePreviewPageIndex++;
            await LoadPdfMergePreviewAsync(_pdfMergeSelectedItem.FilePath, _pdfMergePreviewPageIndex);
        }
    }

    private void PdfMergeDropZone_DragOver(object sender, DragEventArgs e)
    {
        var result = OleFileDropExtractor.Extract(e.Data);
        e.Effects = OleFileDropExtractor.SafeAllowedEffects(result.HasFileDrop);
        e.Handled = true;
    }

    private void PdfMergeDropZone_Drop(object sender, DragEventArgs e)
    {
        var result = OleFileDropExtractor.Extract(e.Data);
        if (result.HasFileDrop)
        {
            AddFilesToPdfMergeQueue(result.Inspection.Files.Select(f => f.Path));
        }
        e.Handled = true;
    }

    private async void PdfMergeExecute_Click(object sender, RoutedEventArgs e)
    {
        if (_pdfMergeItems.Count < 2)
        {
            ShowBanner("At least 2 PDF documents are required to merge.", error: true);
            return;
        }

        if (_pdfMergeItems.Any(i => !i.IsValid))
        {
            ShowBanner("Resolve or remove invalid documents before merging.", error: true);
            return;
        }

        _isPdfMerging = true;
        _pdfMergeCts = new CancellationTokenSource();
        PdfMergeProgressHost.Visibility = Visibility.Visible;
        PdfMergeProgressBar.Value = 0;
        PdfMergeResultCard.Visibility = Visibility.Collapsed;
        PdfMergeExecuteBtn.IsEnabled = false;

        var progress = new Progress<ProcessingProgress>(p =>
        {
            PdfMergeProgressBar.Value = p.Percent;
            PdfMergeProgressText.Text = p.Message;
        });

        try
        {
            var orderedPaths = _pdfMergeItems.Select(i => i.FilePath).ToList();
            var desiredName = !string.IsNullOrWhiteSpace(PdfMergeOutputNameBox.Text)
                ? PdfMergeOutputNameBox.Text.Trim()
                : "Merged_Document.pdf";

            if (!desiredName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                desiredName += ".pdf";
            }

            App.Services.Outputs.EnsureRoot();
            var finalPath = App.Services.Outputs.Allocate(orderedPaths[0], desiredName);

            var mergeStart = DateTimeOffset.UtcNow;
            var mergeSw = Stopwatch.StartNew();

            var mergedPath = await App.Services.PdfMerge.MergeAsync(
                orderedPaths,
                finalPath,
                progress,
                _pdfMergeCts.Token).ConfigureAwait(true);
            mergeSw.Stop();

            _lastMergedPdfPath = mergedPath;

            App.Services.HistoryStore.Record(new OperationRecord(
                Id: Guid.NewGuid().ToString("N"),
                ActionId: "pdf.merge",
                ActionName: "Merge PDFs",
                Category: OperationCategory.Pdf,
                InputPaths: orderedPaths,
                OutputPaths: [mergedPath],
                StartedAtUtc: mergeStart,
                CompletedAtUtc: DateTimeOffset.UtcNow,
                Duration: mergeSw.Elapsed,
                Status: OperationStatus.Completed,
                Options: new Dictionary<string, string> { ["FileCount"] = orderedPaths.Count.ToString() },
                PresetName: null,
                FailureReason: null,
                RetryOfOperationId: null));

            PdfMergeResultTitle.Text = "PDF Merge Completed Successfully";
            PdfMergeResultDetail.Text = $"Combined {orderedPaths.Count} files into {FileSizeFormatter.Format(new FileInfo(mergedPath).Length)} at: {Path.GetFileName(mergedPath)}";
            PdfMergeResultCard.Visibility = Visibility.Visible;
            ShowBanner($"PDF merge complete: {Path.GetFileName(mergedPath)}", error: false);
        }
        catch (OperationCanceledException)
        {
            ShowBanner("PDF merge was cancelled. Original files were not changed.", error: false);
        }
        catch (Exception ex)
        {
            ShowBanner($"Merge error: {ex.Message}", error: true);
        }
        finally
        {
            _isPdfMerging = false;
            PdfMergeProgressHost.Visibility = Visibility.Collapsed;
            PdfMergeExecuteBtn.IsEnabled = true;
            _pdfMergeCts?.Dispose();
            _pdfMergeCts = null;
        }
    }

    private void PdfMergeCancel_Click(object sender, RoutedEventArgs e)
    {
        _pdfMergeCts?.Cancel();
    }

    private void PdfMergeOpenResult_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_lastMergedPdfPath) && File.Exists(_lastMergedPdfPath))
        {
            try
            {
                ShellFolders.OpenFile(_lastMergedPdfPath);
            }
            catch (Exception ex)
            {
                ShowBanner($"Could not open PDF: {ex.Message}", error: true);
            }
        }
    }

    private void PdfMergeRevealResult_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_lastMergedPdfPath) && File.Exists(_lastMergedPdfPath))
        {
            try
            {
                ShellFolders.RevealInExplorer(_lastMergedPdfPath);
            }
            catch (Exception ex)
            {
                ShowBanner($"Could not reveal file: {ex.Message}", error: true);
            }
        }
    }
}
