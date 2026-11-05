using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SmartShift.App.Ui;
using SmartShift.Core.Actions;
using SmartShift.Core.Classification;
using SmartShift.Core.IO;
using SmartShift.Processing;
using SmartShift.Processing.Documents;
using SmartShift.Processing.PresentationStudio.Models;
using SmartShift.Windows.Shell;

namespace SmartShift.App;

public partial class MainWindow : Window
{
    private DocxPreviewViewModel? _docxPreviewModel;
    private string _selectedDocxTab = "pdf";
    private CancellationTokenSource? _docxActionCts;
    private bool _isDocxBusy = false;
    private string? _lastDocxOutputPath;

    public async Task LoadDocxWorkspaceAsync(ClassifiedFile file)
    {
        if (file.Format != DetectedFormat.Docx) return;

        DocxHeaderFileName.Text = Path.GetFileName(file.Path);
        DocxHeaderFileSize.Text = FileSizeFormatter.Format(file.LengthBytes);
        DocxHeaderStatus.Text = "Reading document structure…";
        DocxPdfOutputNameBox.Text = $"{Path.GetFileNameWithoutExtension(file.Path)}.pdf";
        DocxPptxOutputNameBox.Text = $"{Path.GetFileNameWithoutExtension(file.Path)}.pptx";
        DocxCompressOutputNameBox.Text = $"{Path.GetFileNameWithoutExtension(file.Path)}_compressed.docx";
        DocxResultCard.Visibility = Visibility.Collapsed;

        // Populate info panel
        DocxInfoPath.Text = file.Path;
        DocxInfoSize.Text = $"File Size: {FileSizeFormatter.Format(file.LengthBytes)} ({file.LengthBytes:N0} bytes)";

        SelectDocxTab("pdf");

        try
        {
            var content = await App.Services.PresentationStudio.ImportDocumentAsync(file.Path, CancellationToken.None).ConfigureAwait(true);
            _docxPreviewModel = new DocxPreviewViewModel(content);

            DocxPreviewTitle.Text = _docxPreviewModel.DocumentTitle;
            DocxStatWords.Text = _docxPreviewModel.TotalWords.ToString("N0");
            DocxStatChars.Text = _docxPreviewModel.TotalCharacters.ToString("N0");
            DocxStatHeadings.Text = _docxPreviewModel.TotalHeadings.ToString();
            DocxStatSections.Text = _docxPreviewModel.TotalSections.ToString();

            DocxSectionsControl.ItemsSource = _docxPreviewModel.Sections;
            DocxEmptyPreviewHint.Visibility = _docxPreviewModel.Sections.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            DocxInfoStats.Text = $"Words: {_docxPreviewModel.TotalWords:N0} · Characters: {_docxPreviewModel.TotalCharacters:N0} · Headings: {_docxPreviewModel.TotalHeadings} · Sections: {_docxPreviewModel.TotalSections}";
            DocxHeaderStatus.Text = "Document workspace ready";
        }
        catch (Exception ex)
        {
            DocxHeaderStatus.Text = $"Preview limited: {ex.Message}";
            DocxPreviewTitle.Text = Path.GetFileName(file.Path);
            DocxEmptyPreviewHint.Text = $"Structured preview could not be loaded: {ex.Message}";
            DocxEmptyPreviewHint.Visibility = Visibility.Visible;
            DocxSectionsControl.ItemsSource = null;
        }
    }

    private void DocxHeroPdf_Click(object sender, RoutedEventArgs e) => SelectDocxTab("pdf");
    private void DocxHeroPptx_Click(object sender, RoutedEventArgs e) => SelectDocxTab("pptx");
    private void DocxHeroCompress_Click(object sender, RoutedEventArgs e) => SelectDocxTab("compress");

    private void DocxTabPdf_Click(object sender, RoutedEventArgs e) => SelectDocxTab("pdf");
    private void DocxTabPptx_Click(object sender, RoutedEventArgs e) => SelectDocxTab("pptx");
    private void DocxTabCompress_Click(object sender, RoutedEventArgs e) => SelectDocxTab("compress");
    private async void DocxTabPresentation_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedFileItem is not null && File.Exists(_selectedFileItem.File.Path))
        {
            await LoadDocumentIntoPresentationStudioAsync(_selectedFileItem.File.Path);
        }
    }
    private void DocxTabInfo_Click(object sender, RoutedEventArgs e) => SelectDocxTab("info");

    private void SelectDocxTab(string tab)
    {
        _selectedDocxTab = tab;

        // Reset button styles
        SetTabActive(DocxTabPdfBtn, tab == "pdf");
        SetTabActive(DocxTabPptxBtn, tab == "pptx");
        SetTabActive(DocxTabCompressBtn, tab == "compress");
        SetTabActive(DocxTabPresentationBtn, tab == "presentation");
        SetTabActive(DocxTabInfoBtn, tab == "info");

        DocxPdfSettingsPanel.Visibility = tab == "pdf" ? Visibility.Visible : Visibility.Collapsed;
        DocxPptxSettingsPanel.Visibility = tab == "pptx" ? Visibility.Visible : Visibility.Collapsed;
        DocxCompressSettingsPanel.Visibility = tab == "compress" ? Visibility.Visible : Visibility.Collapsed;
        DocxInfoSettingsPanel.Visibility = tab == "info" ? Visibility.Visible : Visibility.Collapsed;

        DocxRunActionBtn.Visibility = tab == "info" ? Visibility.Collapsed : Visibility.Visible;
        DocxRunActionBtnText.Text = tab switch
        {
            "pdf" => "Convert to PDF",
            "pptx" => "Convert to PowerPoint",
            "compress" => "Compress Document",
            _ => "Run Operation"
        };
    }

    private void SetTabActive(Button btn, bool active)
    {
        btn.Style = (Style)FindResource(active ? "PrimaryButton" : "SecondaryButton");
    }

    private async void DocxRunAction_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedFileItem is null || !File.Exists(_selectedFileItem.File.Path))
        {
            ShowBanner("No Word document selected.", error: true);
            return;
        }

        if (_isDocxBusy) return;

        var sourcePath = _selectedFileItem.File.Path;
        _isDocxBusy = true;
        _docxActionCts = new CancellationTokenSource();
        DocxProgressHost.Visibility = Visibility.Visible;
        DocxProgressBar.IsIndeterminate = false;
        DocxProgressBar.Value = 0;
        DocxResultCard.Visibility = Visibility.Collapsed;
        DocxRunActionBtn.IsEnabled = false;

        var progress = new Progress<ProcessingProgress>(p =>
        {
            DocxProgressBar.Value = p.Percent;
            DocxProgressText.Text = p.Message;
        });

        try
        {
            App.Services.Outputs.EnsureRoot();

            if (_selectedDocxTab == "pdf")
            {
                var desiredName = !string.IsNullOrWhiteSpace(DocxPdfOutputNameBox.Text)
                    ? DocxPdfOutputNameBox.Text.Trim()
                    : $"{Path.GetFileNameWithoutExtension(sourcePath)}.pdf";

                if (!desiredName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                {
                    desiredName += ".pdf";
                }

                var finalPath = App.Services.Outputs.Allocate(sourcePath, desiredName);
                var preferWord = DocxPreferWordAppBox.IsChecked == true;

                var options = new DocxPdfConversionOptions(PreferLocalWordEngine: preferWord);
                var generated = await Task.Run(
                    () => DocxToPdfConverter.Convert(sourcePath, finalPath, options, progress, _docxActionCts.Token),
                    _docxActionCts.Token).ConfigureAwait(true);

                _lastDocxOutputPath = generated;
                DocxResultTitle.Text = "PDF Conversion Completed";
                DocxResultDetail.Text = $"Generated PDF ({FileSizeFormatter.Format(new FileInfo(generated).Length)}) at: {Path.GetFileName(generated)}";
                DocxResultCard.Visibility = Visibility.Visible;
                ShowBanner($"DOCX converted to PDF: {Path.GetFileName(generated)}", error: false);
            }
            else if (_selectedDocxTab == "pptx")
            {
                var desiredName = !string.IsNullOrWhiteSpace(DocxPptxOutputNameBox.Text)
                    ? DocxPptxOutputNameBox.Text.Trim()
                    : $"{Path.GetFileNameWithoutExtension(sourcePath)}.pptx";

                if (!desiredName.EndsWith(".pptx", StringComparison.OrdinalIgnoreCase))
                {
                    desiredName += ".pptx";
                }

                var finalPath = App.Services.Outputs.Allocate(sourcePath, desiredName);
                var theme = DocxPptxThemeBox.SelectedIndex switch
                {
                    1 => PresentationTheme.ClassicNavy,
                    2 => PresentationTheme.ModernMinimal,
                    _ => PresentationTheme.DarkSlate
                };
                var aspect = DocxPptxAspectBox.SelectedIndex == 1 ? SlideAspectRatio.Standard4x3 : SlideAspectRatio.Widescreen16x9;
                var titleSlide = DocxPptxTitleSlideBox.IsChecked == true;

                var options = new DocxPptxConversionOptions(
                    Theme: theme,
                    AspectRatio: aspect,
                    GenerateTitleSlide: titleSlide);

                var generated = await DocxToPptxConverter.ConvertAsync(
                    sourcePath,
                    finalPath,
                    options,
                    progress,
                    _docxActionCts.Token).ConfigureAwait(true);

                _lastDocxOutputPath = generated;
                DocxResultTitle.Text = "PowerPoint Conversion Completed";
                DocxResultDetail.Text = $"Generated editable PPTX ({FileSizeFormatter.Format(new FileInfo(generated).Length)}) at: {Path.GetFileName(generated)}";
                DocxResultCard.Visibility = Visibility.Visible;
                ShowBanner($"DOCX converted to PPTX: {Path.GetFileName(generated)}", error: false);
            }
            else if (_selectedDocxTab == "compress")
            {
                var desiredName = !string.IsNullOrWhiteSpace(DocxCompressOutputNameBox.Text)
                    ? DocxCompressOutputNameBox.Text.Trim()
                    : $"{Path.GetFileNameWithoutExtension(sourcePath)}_compressed.docx";

                if (!desiredName.EndsWith(".docx", StringComparison.OrdinalIgnoreCase))
                {
                    desiredName += ".docx";
                }

                var finalPath = App.Services.Outputs.Allocate(sourcePath, desiredName);
                var report = await Task.Run(
                    () => DocxCompressor.Compress(sourcePath, finalPath, progress, _docxActionCts.Token),
                    _docxActionCts.Token).ConfigureAwait(true);

                _lastDocxOutputPath = report.OutputPath;
                DocxResultTitle.Text = report.IsReduction ? "Document Compression Succeeded" : "Document Compression Completed";
                DocxResultDetail.Text = report.SummaryMessage;
                DocxResultCard.Visibility = Visibility.Visible;
                ShowBanner(report.SummaryMessage, error: !report.IsReduction && report.BytesSaved < 0);
            }
        }
        catch (OperationCanceledException)
        {
            ShowBanner("Document operation was cancelled. Original file was not changed.", error: false);
        }
        catch (Exception ex)
        {
            ShowBanner($"Operation failed: {ex.Message}", error: true);
        }
        finally
        {
            _isDocxBusy = false;
            DocxProgressHost.Visibility = Visibility.Collapsed;
            DocxRunActionBtn.IsEnabled = true;
            _docxActionCts?.Dispose();
            _docxActionCts = null;
        }
    }

    private void DocxCancel_Click(object sender, RoutedEventArgs e)
    {
        _docxActionCts?.Cancel();
    }

    private void DocxOpenResult_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_lastDocxOutputPath) && File.Exists(_lastDocxOutputPath))
        {
            try
            {
                ShellFolders.OpenFile(_lastDocxOutputPath);
            }
            catch (Exception ex)
            {
                ShowBanner($"Could not open file: {ex.Message}", error: true);
            }
        }
    }

    private void DocxRevealResult_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_lastDocxOutputPath) && File.Exists(_lastDocxOutputPath))
        {
            try
            {
                ShellFolders.RevealInExplorer(_lastDocxOutputPath);
            }
            catch (Exception ex)
            {
                ShowBanner($"Could not reveal file: {ex.Message}", error: true);
            }
        }
    }
}
