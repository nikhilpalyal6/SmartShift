using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using SkiaSharp;
using SmartShift.App;
using SmartShift.App.Ui;
using SmartShift.Core.Actions;
using SmartShift.Core.Classification;
using Xunit;
using AppClass = SmartShift.App.App;

namespace SmartShift.Windows.Tests;

public sealed class MainWindowSliderSyncTests
{
    private static void RunOnSta(Action action)
    {
        Exception? threadEx = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = Application.Current ?? new Application();
                System.Threading.SynchronizationContext.SetSynchronizationContext(
                    new System.Windows.Threading.DispatcherSynchronizationContext(System.Windows.Threading.Dispatcher.CurrentDispatcher));

                var themeUri = new Uri("pack://application:,,,/SmartShift.App;component/Themes/AppTheme.xaml", UriKind.Absolute);
                if (!app.Resources.MergedDictionaries.Any(d => d.Source == themeUri))
                {
                    app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = themeUri });
                }

                if (AppClass.Services is null)
                {
                    var prop = typeof(AppClass).GetProperty("Services", BindingFlags.Public | BindingFlags.Static);
                    prop?.SetValue(null, new AppServices(AppClass.Logger));
                }

                action();
            }
            catch (Exception ex)
            {
                threadEx = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (threadEx is not null)
        {
            throw new AggregateException("STA thread execution failed", threadEx);
        }
    }

    [Fact]
    public void MainWindow_ConstructsWithoutNullReferenceException()
    {
        RunOnSta(() =>
        {
            var window = new MainWindow();
            Assert.NotNull(window);
            window.Close();
        });
    }

    [Fact]
    public void MainWindow_CanOpenCloseAndReopenNormally()
    {
        RunOnSta(() =>
        {
            var window1 = new MainWindow();
            Assert.NotNull(window1);
            window1.Show();
            window1.Close();

            var window2 = new MainWindow();
            Assert.NotNull(window2);
            window2.Show();
            window2.Close();
        });
    }

    [Fact]
    public void MainWindow_SettingsControls_InitializeSafely()
    {
        RunOnSta(() =>
        {
            var window = new MainWindow();
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;

            var compressSlider = (Slider)typeof(MainWindow).GetField("CompressQualitySlider", flags)!.GetValue(window)!;
            var compressBox = (TextBox)typeof(MainWindow).GetField("CompressQualityBox", flags)!.GetValue(window)!;
            var webpSlider = (Slider)typeof(MainWindow).GetField("WebpQualitySlider", flags)!.GetValue(window)!;
            var webpBox = (TextBox)typeof(MainWindow).GetField("WebpQualityBox", flags)!.GetValue(window)!;
            var resizeKeepAspect = (CheckBox)typeof(MainWindow).GetField("ResizeKeepAspectCheck", flags)!.GetValue(window)!;
            var resizeWidth = (TextBox)typeof(MainWindow).GetField("ResizeWidthBox", flags)!.GetValue(window)!;
            var resizeHeight = (TextBox)typeof(MainWindow).GetField("ResizeHeightBox", flags)!.GetValue(window)!;

            Assert.NotNull(compressSlider);
            Assert.NotNull(compressBox);
            Assert.NotNull(webpSlider);
            Assert.NotNull(webpBox);
            Assert.NotNull(resizeKeepAspect);
            Assert.NotNull(resizeWidth);
            Assert.NotNull(resizeHeight);

            Assert.Equal(80, (int)compressSlider.Value);
            Assert.Equal("80", compressBox.Text);
            Assert.Equal(80, (int)webpSlider.Value);
            Assert.Equal("80", webpBox.Text);
            Assert.True(resizeKeepAspect.IsChecked);
            Assert.Equal("1920", resizeWidth.Text);
            Assert.Equal("1080", resizeHeight.Text);

            window.Close();
        });
    }

    [Fact]
    public void MainWindow_RadialActionMenu_RendersAndComputesLayoutCorrectly()
    {
        RunOnSta(() =>
        {
            var window = new MainWindow();
            window.Width = 1000;
            window.Height = 700;
            window.Measure(new Size(1000, 700));
            window.Arrange(new Rect(0, 0, 1000, 700));

            var flags = BindingFlags.Instance | BindingFlags.NonPublic;

            // Ingest a dummy PDF file
            var tempDir = Path.Combine(Path.GetTempPath(), "smartshift-ui-radial-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var testPdf = Path.Combine(tempDir, "radial-test.pdf");

            using (var doc = new PdfSharp.Pdf.PdfDocument())
            {
                doc.AddPage();
                doc.Save(testPdf);
            }

            var ingestMethod = typeof(MainWindow).GetMethod("IngestAsync", flags)!;
            var ingestTask = (Task)ingestMethod.Invoke(window, new object[] { new[] { testPdf } })!;
            WaitForTask(ingestTask);

            var radialItemsField = typeof(MainWindow).GetField("_radialItems", flags)!;
            var radialItems = (System.Collections.ObjectModel.ObservableCollection<RadialActionItem>)radialItemsField.GetValue(window)!;

            // For PDF, RecommendationEngine returns 7 items: Show Info, Compress, Export, Extract, Word, PowerPoint, Preview
            Assert.Equal(7, radialItems.Count);
            Assert.Equal(ActionIds.ShowPdfInfo, radialItems[0].ActionId);
            Assert.True(radialItems[0].IsAvailable);
            Assert.Equal(ActionIds.CompressPdf, radialItems[1].ActionId);
            Assert.True(radialItems[1].IsAvailable);
            Assert.Equal(ActionIds.ExportPdfPages, radialItems[2].ActionId);
            Assert.True(radialItems[2].IsAvailable);
            Assert.Equal("Export", radialItems[2].Title);
            Assert.Equal("IconExportGeometry", radialItems[2].IconKey);
            Assert.Equal(ActionIds.ExtractPdfText, radialItems[3].ActionId);
            Assert.True(radialItems[3].IsAvailable);
            Assert.Equal("Extract", radialItems[3].Title);
            Assert.Equal("IconDocumentGeometry", radialItems[3].IconKey);
            Assert.Equal(ActionIds.ConvertPdfToDocx, radialItems[4].ActionId);
            Assert.True(radialItems[4].IsAvailable);
            Assert.Equal("Word", radialItems[4].Title);
            Assert.Equal("IconDocumentGeometry", radialItems[4].IconKey);
            Assert.Equal(ActionIds.ConvertPdfToPptx, radialItems[5].ActionId);
            Assert.True(radialItems[5].IsAvailable);
            Assert.Equal("PowerPoint", radialItems[5].Title);
            Assert.Equal("IconPresentationGeometry", radialItems[5].IconKey);
            Assert.Equal(ActionIds.PreviewPdf, radialItems[6].ActionId);
            Assert.True(radialItems[6].IsAvailable);

            window.Close();

            try { File.Delete(testPdf); Directory.Delete(tempDir, true); } catch { }
        });
    }

    [Fact]
    public void CompressQualitySliderAndBox_SynchronizeBidirectionally()
    {
        RunOnSta(() =>
        {
            var window = new MainWindow();

            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var slider = (Slider)typeof(MainWindow).GetField("CompressQualitySlider", flags)!.GetValue(window)!;
            var box = (TextBox)typeof(MainWindow).GetField("CompressQualityBox", flags)!.GetValue(window)!;

            // Initial defaults: Slider = 80, Box = "80"
            Assert.Equal(80, (int)slider.Value);
            Assert.Equal("80", box.Text);

            // Slider -> Box
            slider.Value = 65;
            Assert.Equal("65", box.Text);

            // Box -> Slider
            box.Text = "42";
            Assert.Equal(42, (int)slider.Value);

            // Invalid text should not alter slider
            box.Text = "invalid";
            Assert.Equal(42, (int)slider.Value);

            // Out-of-bounds text should not alter slider
            box.Text = "150";
            Assert.Equal(42, (int)slider.Value);

            window.Close();
        });
    }

    [Fact]
    public void WebpQualitySliderAndBox_SynchronizeBidirectionally()
    {
        RunOnSta(() =>
        {
            var window = new MainWindow();

            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var slider = (Slider)typeof(MainWindow).GetField("WebpQualitySlider", flags)!.GetValue(window)!;
            var box = (TextBox)typeof(MainWindow).GetField("WebpQualityBox", flags)!.GetValue(window)!;

            Assert.Equal(80, (int)slider.Value);
            Assert.Equal("80", box.Text);

            slider.Value = 92;
            Assert.Equal("92", box.Text);

            box.Text = "55";
            Assert.Equal(55, (int)slider.Value);

            window.Close();
        });
    }

    private static void WaitForTask(Task task)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        task.ContinueWith(_ => frame.Continue = false);
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    [Fact]
    public void CompressImage_ViaMainWindow_ProducesValidOutputAndPreservesSource()
    {
        RunOnSta(() =>
        {
            // Create a valid 1x1 PNG file
            var tempDir = Path.Combine(Path.GetTempPath(), "smartshift-ui-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var testPng = Path.Combine(tempDir, "test-image.png");
            File.WriteAllBytes(testPng, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="));

            var originalSha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(testPng)));

            var window = new MainWindow();

            // Ingest file asynchronously while pumping dispatcher
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var ingestMethod = typeof(MainWindow).GetMethod("IngestAsync", flags)!;
            var ingestTask = (Task)ingestMethod.Invoke(window, new object[] { new[] { testPng } })!;
            WaitForTask(ingestTask);

            var filesField = typeof(MainWindow).GetField("_files", flags)!;
            var files = (System.Collections.ObjectModel.ObservableCollection<QueuedFileItem>)filesField.GetValue(window)!;
            Assert.Single(files);

            var fileList = (ListBox)typeof(MainWindow).GetField("FileList", flags)!.GetValue(window)!;
            fileList.SelectedItem = files[0];

            // Ensure selection changed is processed
            var selectionMethod = typeof(MainWindow).GetMethod("FileList_SelectionChanged", flags)!;
            selectionMethod.Invoke(window, new object[] { fileList, null! });

            // Select compression action from radial items
            var radialItemsField = typeof(MainWindow).GetField("_radialItems", flags)!;
            var radialItems = (System.Collections.ObjectModel.ObservableCollection<RadialActionItem>)radialItemsField.GetValue(window)!;
            var compressRadial = radialItems.First(r => r.ActionId == ActionIds.CompressImage);
            var selectRadialMethod = typeof(MainWindow).GetMethod("SelectRadialAction", flags)!;
            selectRadialMethod.Invoke(window, new object[] { compressRadial });

            // Verify compression action is selected
            var selectedActionField = typeof(MainWindow).GetField("_selectedAction", flags)!;
            var selectedAction = (ActionRecommendation?)selectedActionField.GetValue(window);
            Assert.NotNull(selectedAction);
            Assert.Equal(ActionIds.CompressImage, selectedAction.ActionId);

            // Change quality to 70 and verify box updates
            var slider = (Slider)typeof(MainWindow).GetField("CompressQualitySlider", flags)!.GetValue(window)!;
            var box = (TextBox)typeof(MainWindow).GetField("CompressQualityBox", flags)!.GetValue(window)!;
            slider.Value = 70;
            Assert.Equal("70", box.Text);

            // Invoke RunAction_Click
            var runMethod = typeof(MainWindow).GetMethod("RunAction_Click", flags)!;
            runMethod.Invoke(window, new object[] { window, new RoutedEventArgs() });

            // Wait briefly for the async operation to complete while pumping dispatcher
            var lastOutputPathField = typeof(MainWindow).GetField("_lastOutputPath", flags)!;
            string? outputPath = null;
            for (int i = 0; i < 50; i++)
            {
                var delayFrame = new System.Windows.Threading.DispatcherFrame();
                Task.Delay(100).ContinueWith(_ => delayFrame.Continue = false);
                System.Windows.Threading.Dispatcher.PushFrame(delayFrame);

                outputPath = (string?)lastOutputPathField.GetValue(window);
                if (outputPath is not null && File.Exists(outputPath))
                {
                    break;
                }
            }

            Assert.NotNull(outputPath);
            Assert.True(File.Exists(outputPath));

            // Verify source file is byte-for-byte unchanged
            var currentSha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(testPng)));
            Assert.Equal(originalSha, currentSha);

            window.Close();

            // Cleanup
            try { File.Delete(outputPath); } catch { }
            try { Directory.Delete(tempDir, true); } catch { }
        });
    }

    [Fact]
    public void PdfTargetSizePresets_UpdateBoxAndUnit()
    {
        RunOnSta(() =>
        {
            var window = new MainWindow();
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;

            var box = (TextBox)typeof(MainWindow).GetField("PdfTargetSizeBox", flags)!.GetValue(window)!;
            var hint = (TextBlock)typeof(MainWindow).GetField("PdfTargetValidationHint", flags)!.GetValue(window)!;
            var unitField = typeof(MainWindow).GetField("_pdfTargetUnit", flags)!;

            var preset500 = (Button)typeof(MainWindow).GetField("PdfPreset500Kb", flags)!.GetValue(window)!;
            var preset1 = (Button)typeof(MainWindow).GetField("PdfPreset1Mb", flags)!.GetValue(window)!;
            var preset2 = (Button)typeof(MainWindow).GetField("PdfPreset2Mb", flags)!.GetValue(window)!;
            var preset5 = (Button)typeof(MainWindow).GetField("PdfPreset5Mb", flags)!.GetValue(window)!;
            var presetCustom = (Button)typeof(MainWindow).GetField("PdfPresetCustom", flags)!.GetValue(window)!;

            // 500 KB preset
            preset500.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("500", box.Text);
            Assert.Equal("KB", (string)unitField.GetValue(window)!);
            Assert.Contains("500", hint.Text);

            // 1 MB preset
            preset1.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("1", box.Text);
            Assert.Equal("MB", (string)unitField.GetValue(window)!);

            // 2 MB preset
            preset2.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("2", box.Text);
            Assert.Equal("MB", (string)unitField.GetValue(window)!);

            // 5 MB preset
            preset5.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("5", box.Text);
            Assert.Equal("MB", (string)unitField.GetValue(window)!);

            // Custom preset
            presetCustom.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            box.Text = "750";
            Assert.Equal("750", box.Text);

            window.Close();
        });
    }

    [Fact]
    public void PdfTargetValidation_DisablesRunButtonOnInvalidInput()
    {
        RunOnSta(() =>
        {
            var window = new MainWindow();
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;

            // Ingest a dummy PDF file so CompressPdf action can be active
            var tempDir = Path.Combine(Path.GetTempPath(), "smartshift-ui-valid-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var testPdf = Path.Combine(tempDir, "test.pdf");

            using (var doc = new PdfSharp.Pdf.PdfDocument())
            {
                doc.AddPage();
                doc.Save(testPdf);
            }

            var ingestMethod = typeof(MainWindow).GetMethod("IngestAsync", flags)!;
            var ingestTask = (Task)ingestMethod.Invoke(window, new object[] { new[] { testPdf } })!;
            WaitForTask(ingestTask);

            var files = (System.Collections.ObjectModel.ObservableCollection<QueuedFileItem>)typeof(MainWindow).GetField("_files", flags)!.GetValue(window)!;
            var fileList = (ListBox)typeof(MainWindow).GetField("FileList", flags)!.GetValue(window)!;
            fileList.SelectedItem = files[0];
            var selectionMethod = typeof(MainWindow).GetMethod("FileList_SelectionChanged", flags)!;
            selectionMethod.Invoke(window, new object[] { fileList, null! });

            var radialItems = (System.Collections.ObjectModel.ObservableCollection<RadialActionItem>)typeof(MainWindow).GetField("_radialItems", flags)!.GetValue(window)!;
            var compressRadial = radialItems.First(r => r.ActionId == ActionIds.CompressPdf);
            var selectRadialMethod = typeof(MainWindow).GetMethod("SelectRadialAction", flags)!;
            selectRadialMethod.Invoke(window, new object[] { compressRadial });

            var box = (TextBox)typeof(MainWindow).GetField("PdfTargetSizeBox", flags)!.GetValue(window)!;
            var runBtn = (Button)typeof(MainWindow).GetField("RunButton", flags)!.GetValue(window)!;
            var hint = (TextBlock)typeof(MainWindow).GetField("PdfTargetValidationHint", flags)!.GetValue(window)!;

            // Valid input -> Run enabled
            box.Text = "2";
            Assert.True(runBtn.IsEnabled);

            // Zero -> Run disabled
            box.Text = "0";
            Assert.False(runBtn.IsEnabled);
            Assert.Contains("positive", hint.Text, StringComparison.OrdinalIgnoreCase);

            // Negative -> Run disabled
            box.Text = "-5";
            Assert.False(runBtn.IsEnabled);

            // Non-numeric -> Run disabled
            box.Text = "abc";
            Assert.False(runBtn.IsEnabled);

            // Back to valid -> Run re-enabled
            box.Text = "1.5";
            Assert.True(runBtn.IsEnabled);

            window.Close();
            try { File.Delete(testPdf); Directory.Delete(tempDir, true); } catch { }
        });
    }

    [Fact]
    public void PdfImageQualityButtons_UpdateQualityPolicy()
    {
        RunOnSta(() =>
        {
            var window = new MainWindow();
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;

            var qualityField = typeof(MainWindow).GetField("_pdfQuality", flags)!;
            var btnBalanced = (Button)typeof(MainWindow).GetField("PdfQualityBalancedBtn", flags)!.GetValue(window)!;
            var btnBest = (Button)typeof(MainWindow).GetField("PdfQualityBestBtn", flags)!.GetValue(window)!;
            var btnSmallest = (Button)typeof(MainWindow).GetField("PdfQualitySmallestBtn", flags)!.GetValue(window)!;
            var btnOff = (Button)typeof(MainWindow).GetField("PdfQualityOffBtn", flags)!.GetValue(window)!;

            // Initial default
            Assert.Equal("balanced", (string)qualityField.GetValue(window)!);

            // Best
            btnBest.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("best", (string)qualityField.GetValue(window)!);

            // Smallest
            btnSmallest.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("smallest", (string)qualityField.GetValue(window)!);

            // Off
            btnOff.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("off", (string)qualityField.GetValue(window)!);

            // Balanced
            btnBalanced.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("balanced", (string)qualityField.GetValue(window)!);

            window.Close();
        });
    }

    [Fact]
    public void CompressPdf_ViaMainWindow_ProducesValidOutputAndPreservesSource()
    {
        RunOnSta(() =>
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "smartshift-ui-pdf-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var testPdf = Path.Combine(tempDir, "document.pdf");

            using (var doc = new PdfSharp.Pdf.PdfDocument())
            {
                var page = doc.AddPage();
                using var gfx = PdfSharp.Drawing.XGraphics.FromPdfPage(page);
                gfx.DrawString("SmartShift PDF UI Verification", new PdfSharp.Drawing.XFont("Arial", 12, PdfSharp.Drawing.XFontStyleEx.Regular), PdfSharp.Drawing.XBrushes.Black, new PdfSharp.Drawing.XPoint(40, 40));
                doc.Save(testPdf);
            }

            var originalSha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(testPdf)));

            var window = new MainWindow();

            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var ingestMethod = typeof(MainWindow).GetMethod("IngestAsync", flags)!;
            var ingestTask = (Task)ingestMethod.Invoke(window, new object[] { new[] { testPdf } })!;
            WaitForTask(ingestTask);

            var filesField = typeof(MainWindow).GetField("_files", flags)!;
            var files = (System.Collections.ObjectModel.ObservableCollection<QueuedFileItem>)filesField.GetValue(window)!;
            Assert.Single(files);

            var fileList = (ListBox)typeof(MainWindow).GetField("FileList", flags)!.GetValue(window)!;
            fileList.SelectedItem = files[0];

            var selectionMethod = typeof(MainWindow).GetMethod("FileList_SelectionChanged", flags)!;
            selectionMethod.Invoke(window, new object[] { fileList, null! });

            // Check radial items: CompressPdf must be available, PreviewPdf must be unavailable
            var radialItemsField = typeof(MainWindow).GetField("_radialItems", flags)!;
            var radialItems = (System.Collections.ObjectModel.ObservableCollection<RadialActionItem>)radialItemsField.GetValue(window)!;

            var compressRadial = radialItems.First(r => r.ActionId == ActionIds.CompressPdf);
            var previewRadial = radialItems.First(r => r.ActionId == ActionIds.PreviewPdf);
            Assert.True(compressRadial.IsAvailable);
            Assert.True(previewRadial.IsAvailable);

            var selectRadialMethod = typeof(MainWindow).GetMethod("SelectRadialAction", flags)!;

            // When preview is selected, unavailable view is shown
            selectRadialMethod.Invoke(window, new object[] { previewRadial });
            var pdfPreviewOptionsView = (StackPanel)typeof(MainWindow).GetField("PdfPreviewOptionsView", flags)!.GetValue(window)!;
            Assert.Equal(Visibility.Visible, pdfPreviewOptionsView.Visibility);

            // When compress is selected, compress options view is shown
            selectRadialMethod.Invoke(window, new object[] { compressRadial });
            var pdfCompressOptionsView = (StackPanel)typeof(MainWindow).GetField("PdfCompressOptionsView", flags)!.GetValue(window)!;
            Assert.Equal(Visibility.Visible, pdfCompressOptionsView.Visibility);
            Assert.Equal(Visibility.Collapsed, pdfPreviewOptionsView.Visibility);

            // Execute compression action
            var runMethod = typeof(MainWindow).GetMethod("RunAction_Click", flags)!;
            runMethod.Invoke(window, new object[] { window, new RoutedEventArgs() });

            var lastOutputPathField = typeof(MainWindow).GetField("_lastOutputPath", flags)!;
            string? outputPath = null;
            for (int i = 0; i < 50; i++)
            {
                var delayFrame = new System.Windows.Threading.DispatcherFrame();
                Task.Delay(100).ContinueWith(_ => delayFrame.Continue = false);
                System.Windows.Threading.Dispatcher.PushFrame(delayFrame);

                outputPath = (string?)lastOutputPathField.GetValue(window);
                if (outputPath is not null && File.Exists(outputPath))
                {
                    break;
                }
            }

            Assert.NotNull(outputPath);
            Assert.True(File.Exists(outputPath));

            // Verify output is valid PDF with 1 page
            using (var verifyDoc = PdfSharp.Pdf.IO.PdfReader.Open(outputPath, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import))
            {
                Assert.Equal(1, verifyDoc.PageCount);
            }

            // Verify original PDF unchanged
            var currentSha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(testPdf)));
            Assert.Equal(originalSha, currentSha);

            window.Close();

            try { File.Delete(outputPath); } catch { }
            try { Directory.Delete(tempDir, true); } catch { }
        });
    }

    [Fact]
    public void DisplayResult_TargetReached_RendersTargetReachedTitleAndGreenBadge()
    {
        RunOnSta(() =>
        {
            var window = new MainWindow();
            var summary = new ProcessingResultSummary(
                status: "Completed — Target reached.",
                succeeded: true,
                wasCancelled: false,
                originalUnchanged: true,
                outputPath: @"C:\output\doc.pdf",
                originalSizeBytes: 4_000_000,
                outputSizeBytes: 1_800_000,
                outputDimensions: "612 × 792 pt (5 pages)",
                outputFormat: "PDF",
                encoderNote: null,
                metadataNote: null,
                sizeNote: "Reduced by 55%",
                targetSizeBytes: 2_000_000,
                targetSizeText: "2.0 MB",
                targetReached: true,
                targetStatusNote: "Target 2.0 MB met.",
                imagesOptimizedCount: 4,
                imagesDiscoveredCount: 4,
                imagesEligibleCount: 4,
                limitsExplanation: "Target met with lossless stream and image optimization.");

            var method = typeof(MainWindow).GetMethod("DisplayResult", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);
            method.Invoke(window, [summary]);

            var titleBlock = (TextBlock)window.FindName("ResultStatusTitle");
            var badgeText = (TextBlock)window.FindName("ResultBadgeText");
            var targetBox = (Border)window.FindName("TargetDiagnosticsBox");

            Assert.Equal("Completed — Target reached", titleBlock.Text);
            Assert.Equal("TARGET REACHED", badgeText.Text);
            Assert.Equal(Visibility.Visible, targetBox.Visibility);

            window.Close();
        });
    }

    [Fact]
    public void DisplayResult_TargetNotReached_RendersTargetNotReachedTitleAndAmberBadge()
    {
        RunOnSta(() =>
        {
            var window = new MainWindow();
            var summary = new ProcessingResultSummary(
                status: "Completed — Target not reached.",
                succeeded: true,
                wasCancelled: false,
                originalUnchanged: true,
                outputPath: @"C:\output\doc.pdf",
                originalSizeBytes: 5_169_472,
                outputSizeBytes: 4_970_240,
                outputDimensions: "612 × 792 pt (126 pages)",
                outputFormat: "PDF",
                encoderNote: null,
                metadataNote: null,
                sizeNote: "Saved 199 KB (3.9% reduction)",
                targetSizeBytes: 2_097_152,
                targetSizeText: "2.0 MB",
                targetReached: false,
                targetStatusNote: "Target 2.0 MB not reached (4.74 MB).",
                imagesOptimizedCount: 0,
                imagesDiscoveredCount: 12,
                imagesEligibleCount: 0,
                skippedReasonsSummary: "Unsupported filter (FlateDecode): 12",
                limitsExplanation: "Found 12 embedded image(s), but none were eligible for safe JPEG optimization (FlateDecode: 12). Preserved untouched to prevent quality loss.",
                stageApplied: "Stage A: Lossless Stream Optimization");

            var method = typeof(MainWindow).GetMethod("DisplayResult", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);
            method.Invoke(window, [summary]);

            var titleBlock = (TextBlock)window.FindName("ResultStatusTitle");
            var badgeText = (TextBlock)window.FindName("ResultBadgeText");
            var targetBox = (Border)window.FindName("TargetDiagnosticsBox");
            var goalLine = (TextBlock)window.FindName("TargetGoalLine");
            var diagLine = (TextBlock)window.FindName("TargetDiagnosticLine");

            Assert.Equal("Completed — Target not reached", titleBlock.Text);
            Assert.Equal("TARGET NOT REACHED", badgeText.Text);
            Assert.Equal(Visibility.Visible, targetBox.Visibility);
            Assert.Contains("2.0 MB", goalLine.Text);
            Assert.Contains("(not reached)", goalLine.Text);
            Assert.Contains("12", diagLine.Text);

            window.Close();
        });
    }

    [Fact]
    public void DisplayResult_OperationFailed_RendersFailedTitle()
    {
        RunOnSta(() =>
        {
            var window = new MainWindow();
            var summary = new ProcessingResultSummary(
                status: "File could not be parsed.",
                succeeded: false,
                wasCancelled: false,
                originalUnchanged: true,
                outputPath: null,
                originalSizeBytes: 1000,
                outputSizeBytes: 0,
                outputDimensions: null,
                outputFormat: null,
                encoderNote: null,
                metadataNote: null,
                sizeNote: null);

            var method = typeof(MainWindow).GetMethod("DisplayResult", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);
            method.Invoke(window, [summary]);

            var errorTitle = (TextBlock)window.FindName("ErrorResultTitle");
            Assert.Equal("Failed — No valid output produced", errorTitle.Text);

            window.Close();
        });
    }

    [Fact]
    public void DisplayResult_OperationCancelled_RendersCancelledTitle()
    {
        RunOnSta(() =>
        {
            var window = new MainWindow();
            var summary = new ProcessingResultSummary(
                status: "Cancelled",
                succeeded: false,
                wasCancelled: true,
                originalUnchanged: true,
                outputPath: null,
                originalSizeBytes: 1000,
                outputSizeBytes: 0,
                outputDimensions: null,
                outputFormat: null,
                encoderNote: null,
                metadataNote: null,
                sizeNote: null);

            var method = typeof(MainWindow).GetMethod("DisplayResult", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);
            method.Invoke(window, [summary]);

            var errorTitle = (TextBlock)window.FindName("ErrorResultTitle");
            Assert.Equal("Cancelled", errorTitle.Text);

            window.Close();
        });
    }

    [Fact]
    public void MainWindow_PdfExportOptionsView_InteractionsAndGathering()
    {
        RunOnSta(() =>
        {
            var window = new MainWindow();
            window.Width = 1000;
            window.Height = 700;
            window.Measure(new Size(1000, 700));
            window.Arrange(new Rect(0, 0, 1000, 700));

            var flags = BindingFlags.Instance | BindingFlags.NonPublic;

            // Ingest a dummy PDF file
            var tempDir = Path.Combine(Path.GetTempPath(), "smartshift-ui-export-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var testPdf = Path.Combine(tempDir, "export-test.pdf");

            using (var doc = new PdfSharp.Pdf.PdfDocument())
            {
                doc.AddPage();
                doc.Save(testPdf);
            }

            var ingestMethod = typeof(MainWindow).GetMethod("IngestAsync", flags)!;
            var ingestTask = (Task)ingestMethod.Invoke(window, new object[] { new[] { testPdf } })!;
            WaitForTask(ingestTask);

            var radialItemsField = typeof(MainWindow).GetField("_radialItems", flags)!;
            var radialItems = (System.Collections.ObjectModel.ObservableCollection<RadialActionItem>)radialItemsField.GetValue(window)!;
            var exportItem = radialItems.First(r => r.ActionId == ActionIds.ExportPdfPages);

            // Select Export action
            var selectMethod = typeof(MainWindow).GetMethod("SelectRadialAction", flags)!;
            selectMethod.Invoke(window, new object[] { exportItem });

            var exportView = (StackPanel)window.FindName("PdfExportOptionsView");
            Assert.Equal(Visibility.Visible, exportView.Visibility);

            var jpegQualityHost = (StackPanel)window.FindName("PdfExportQualityHost");
            Assert.Equal(Visibility.Collapsed, jpegQualityHost.Visibility); // PNG by default

            // Toggle to JPEG
            var jpegBtn = (Button)window.FindName("PdfExportJpegBtn");
            var formatClickMethod = typeof(MainWindow).GetMethod("PdfExportFormat_Click", flags)!;
            formatClickMethod.Invoke(window, new object[] { jpegBtn, new RoutedEventArgs() });
            Assert.Equal(Visibility.Visible, jpegQualityHost.Visibility);

            // Change DPI to 300
            var dpi300Btn = (Button)window.FindName("PdfExportDpi300Btn");
            var dpiClickMethod = typeof(MainWindow).GetMethod("PdfExportDpi_Click", flags)!;
            dpiClickMethod.Invoke(window, new object[] { dpi300Btn, new RoutedEventArgs() });

            // Set page range to 1
            var rangeBox = (TextBox)window.FindName("PdfExportRangeBox");
            rangeBox.Text = "1";

            // Gather options
            var gatherMethod = typeof(MainWindow).GetMethod("GatherOptions", flags)!;
            var options = (Dictionary<string, string>)gatherMethod.Invoke(window, new object[] { ActionIds.ExportPdfPages })!;
            Assert.Equal("jpeg", options["format"]);
            Assert.Equal("300", options["dpi"]);
            Assert.Equal("1", options["pageRange"]);
            Assert.Equal("85", options["quality"]);

            // Invalid page range disables RunButton
            var runBtn = (Button)window.FindName("RunButton");
            rangeBox.Text = "invalid-range";
            Assert.False(runBtn.IsEnabled);

            // Restore valid range
            rangeBox.Text = "all";
            Assert.True(runBtn.IsEnabled);

            window.Close();
            try { File.Delete(testPdf); Directory.Delete(tempDir, true); } catch { }
        });
    }

    [Fact]
    public void MainWindow_PdfTextOptionsView_InteractionsAndGathering()
    {
        RunOnSta(() =>
        {
            var window = new MainWindow();
            window.Width = 1000;
            window.Height = 700;
            window.Measure(new Size(1000, 700));
            window.Arrange(new Rect(0, 0, 1000, 700));

            var flags = BindingFlags.Instance | BindingFlags.NonPublic;

            var tempDir = Path.Combine(Path.GetTempPath(), "smartshift-ui-text-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var testPdf = Path.Combine(tempDir, "text-test.pdf");

            using (var doc = new PdfSharp.Pdf.PdfDocument())
            {
                doc.AddPage();
                doc.Save(testPdf);
            }

            var ingestMethod = typeof(MainWindow).GetMethod("IngestAsync", flags)!;
            var ingestTask = (Task)ingestMethod.Invoke(window, new object[] { new[] { testPdf } })!;
            WaitForTask(ingestTask);

            var radialItemsField = typeof(MainWindow).GetField("_radialItems", flags)!;
            var radialItems = (System.Collections.ObjectModel.ObservableCollection<RadialActionItem>)radialItemsField.GetValue(window)!;
            var textItem = radialItems.First(r => r.ActionId == ActionIds.ExtractPdfText);

            // Select Extract action
            var selectMethod = typeof(MainWindow).GetMethod("SelectRadialAction", flags)!;
            selectMethod.Invoke(window, new object[] { textItem });

            var textView = (StackPanel)window.FindName("PdfTextOptionsView");
            Assert.Equal(Visibility.Visible, textView.Visibility);

            var rangeBox = (TextBox)window.FindName("PdfTextRangeBox");
            rangeBox.Text = "1-2";

            var gatherMethod = typeof(MainWindow).GetMethod("GatherOptions", flags)!;
            var options = (Dictionary<string, string>)gatherMethod.Invoke(window, new object[] { ActionIds.ExtractPdfText })!;
            Assert.Equal("1-2", options["pageRange"]);

            var runBtn = (Button)window.FindName("RunButton");
            rangeBox.Text = "999-1"; // inverted range
            Assert.False(runBtn.IsEnabled);

            rangeBox.Text = "all";
            Assert.True(runBtn.IsEnabled);

            window.Close();
            try { File.Delete(testPdf); Directory.Delete(tempDir, true); } catch { }
        });
    }

    [Fact]
    public void DisplayResult_PdfExportAndText_RendersCleanCompletedState()
    {
        RunOnSta(() =>
        {
            var window = new MainWindow();
            var summary = new ProcessingResultSummary(
                status: "Completed. Exported 2 page(s) as PNG (150 DPI).",
                succeeded: true,
                wasCancelled: false,
                originalUnchanged: true,
                outputPath: @"C:\dummy\file-page-001.png",
                originalSizeBytes: 50000,
                outputSizeBytes: 80000,
                outputDimensions: "1240 × 1754 px",
                outputFormat: "PNG",
                encoderNote: "Docnet PDFium / SkiaSharp",
                metadataNote: null,
                sizeNote: null,
                actionId: ActionIds.ExportPdfPages,
                customSummaryNote: "Exported 2 page(s) (PNG, 150 DPI) in 45 ms.",
                exportedPagesCount: 2,
                totalPagesCount: 2);

            var method = typeof(MainWindow).GetMethod("DisplayResult", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);
            method.Invoke(window, [summary]);

            var sizeBlock = (FrameworkElement)window.FindName("SizeComparisonBlock");
            Assert.Equal(Visibility.Collapsed, sizeBlock.Visibility);

            var statusTitle = (TextBlock)window.FindName("ResultStatusTitle");
            Assert.Equal("Operation Complete", statusTitle.Text);

            var notesLine = (TextBlock)window.FindName("ResultNotesLine");
            Assert.Equal("Exported 2 page(s) (PNG, 150 DPI) in 45 ms.", notesLine.Text);

            window.Close();
        });
    }

    [Fact]
    public void MainWindow_PdfDocxOptionsView_InteractionsAndGathering()
    {
        RunOnSta(() =>
        {
            var window = new MainWindow();
            window.Width = 1000;
            window.Height = 700;
            window.Measure(new Size(1000, 700));
            window.Arrange(new Rect(0, 0, 1000, 700));

            var flags = BindingFlags.Instance | BindingFlags.NonPublic;

            var tempDir = Path.Combine(Path.GetTempPath(), "smartshift-ui-docx-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var testPdf = Path.Combine(tempDir, "docx-test.pdf");

            using (var doc = new PdfSharp.Pdf.PdfDocument())
            {
                doc.AddPage();
                doc.Save(testPdf);
            }

            var ingestMethod = typeof(MainWindow).GetMethod("IngestAsync", flags)!;
            var ingestTask = (Task)ingestMethod.Invoke(window, new object[] { new[] { testPdf } })!;
            WaitForTask(ingestTask);

            var radialItemsField = typeof(MainWindow).GetField("_radialItems", flags)!;
            var radialItems = (System.Collections.ObjectModel.ObservableCollection<RadialActionItem>)radialItemsField.GetValue(window)!;
            var docxItem = radialItems.First(r => r.ActionId == ActionIds.ConvertPdfToDocx);

            // Select Word action
            var selectMethod = typeof(MainWindow).GetMethod("SelectRadialAction", flags)!;
            selectMethod.Invoke(window, new object[] { docxItem });

            var docxView = (StackPanel)window.FindName("PdfDocxOptionsView");
            Assert.Equal(Visibility.Visible, docxView.Visibility);

            var rangeBox = (TextBox)window.FindName("PdfDocxRangeBox");
            rangeBox.Text = "1";

            var gatherMethod = typeof(MainWindow).GetMethod("GatherOptions", flags)!;
            var options = (Dictionary<string, string>)gatherMethod.Invoke(window, new object[] { ActionIds.ConvertPdfToDocx })!;
            Assert.Equal("1", options["pageRange"]);

            var runBtn = (Button)window.FindName("RunButton");
            rangeBox.Text = "invalid-range";
            Assert.False(runBtn.IsEnabled);

            rangeBox.Text = "all";
            Assert.True(runBtn.IsEnabled);

            window.Close();
            try { File.Delete(testPdf); Directory.Delete(tempDir, true); } catch { }
        });
    }

    [Fact]
    public void MainWindow_PdfPptxOptionsView_InteractionsAndGathering()
    {
        RunOnSta(() =>
        {
            var window = new MainWindow();
            window.Width = 1000;
            window.Height = 700;
            window.Measure(new Size(1000, 700));
            window.Arrange(new Rect(0, 0, 1000, 700));

            var flags = BindingFlags.Instance | BindingFlags.NonPublic;

            var tempDir = Path.Combine(Path.GetTempPath(), "smartshift-ui-pptx-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var testPdf = Path.Combine(tempDir, "pptx-test.pdf");

            using (var doc = new PdfSharp.Pdf.PdfDocument())
            {
                doc.AddPage();
                doc.Save(testPdf);
            }

            var ingestMethod = typeof(MainWindow).GetMethod("IngestAsync", flags)!;
            var ingestTask = (Task)ingestMethod.Invoke(window, new object[] { new[] { testPdf } })!;
            WaitForTask(ingestTask);

            var radialItemsField = typeof(MainWindow).GetField("_radialItems", flags)!;
            var radialItems = (System.Collections.ObjectModel.ObservableCollection<RadialActionItem>)radialItemsField.GetValue(window)!;
            var pptxItem = radialItems.First(r => r.ActionId == ActionIds.ConvertPdfToPptx);

            // Select PowerPoint action
            var selectMethod = typeof(MainWindow).GetMethod("SelectRadialAction", flags)!;
            selectMethod.Invoke(window, new object[] { pptxItem });

            var pptxView = (StackPanel)window.FindName("PdfPptxOptionsView");
            Assert.Equal(Visibility.Visible, pptxView.Visibility);

            // Toggle to Editable mode
            var editableBtn = (Button)window.FindName("PdfPptxModeEditableBtn");
            var modeClickMethod = typeof(MainWindow).GetMethod("PdfPptxMode_Click", flags)!;
            modeClickMethod.Invoke(window, new object[] { editableBtn, new RoutedEventArgs() });

            var rangeBox = (TextBox)window.FindName("PdfPptxRangeBox");
            rangeBox.Text = "1-2";

            var gatherMethod = typeof(MainWindow).GetMethod("GatherOptions", flags)!;
            var options = (Dictionary<string, string>)gatherMethod.Invoke(window, new object[] { ActionIds.ConvertPdfToPptx })!;
            Assert.Equal("editable", options["slideMode"]);
            Assert.Equal("1-2", options["pageRange"]);

            // Toggle back to PageAsImage
            var imageBtn = (Button)window.FindName("PdfPptxModeImageBtn");
            modeClickMethod.Invoke(window, new object[] { imageBtn, new RoutedEventArgs() });
            options = (Dictionary<string, string>)gatherMethod.Invoke(window, new object[] { ActionIds.ConvertPdfToPptx })!;
            Assert.Equal("pageAsImage", options["slideMode"]);

            var runBtn = (Button)window.FindName("RunButton");
            rangeBox.Text = "999-1"; // invalid range
            Assert.False(runBtn.IsEnabled);

            rangeBox.Text = "all";
            Assert.True(runBtn.IsEnabled);

            window.Close();
            try { File.Delete(testPdf); Directory.Delete(tempDir, true); } catch { }
        });
    }

    [Fact]
    public void DisplayResult_PdfDocxAndPptx_RendersCleanCompletedState()
    {
        RunOnSta(() =>
        {
            var window = new MainWindow();
            var docxSummary = new ProcessingResultSummary(
                status: "Completed. Converted 2 page(s) into editable DOCX.",
                succeeded: true,
                wasCancelled: false,
                originalUnchanged: true,
                outputPath: @"C:\dummy\file.docx",
                originalSizeBytes: 50000,
                outputSizeBytes: 30000,
                outputDimensions: "A4",
                outputFormat: "DOCX",
                encoderNote: "DocumentFormat.OpenXml",
                metadataNote: null,
                sizeNote: null,
                actionId: ActionIds.ConvertPdfToDocx,
                customSummaryNote: "Converted 2 page(s) (1,200 characters) into editable DOCX in 80 ms.",
                exportedPagesCount: 0,
                totalPagesCount: 2);

            var method = typeof(MainWindow).GetMethod("DisplayResult", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);
            method.Invoke(window, [docxSummary]);

            var sizeBlock = (FrameworkElement)window.FindName("SizeComparisonBlock");
            Assert.Equal(Visibility.Collapsed, sizeBlock.Visibility);

            var statusTitle = (TextBlock)window.FindName("ResultStatusTitle");
            Assert.Equal("Operation Complete", statusTitle.Text);

            var notesLine = (TextBlock)window.FindName("ResultNotesLine");
            Assert.Equal("Converted 2 page(s) (1,200 characters) into editable DOCX in 80 ms.", notesLine.Text);

            window.Close();
        });
    }

    [Fact]
    public void PdfPreview_SelectionAndOpening_RendersPreviewHostAndToolbarControls()
    {
        RunOnSta(() =>
        {
            var flags = BindingFlags.NonPublic | BindingFlags.Instance;
            var tempDir = Path.Combine(Path.GetTempPath(), "preview-ui-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var testPdf = Path.Combine(tempDir, "preview_test.pdf");

            using (var doc = new PdfSharp.Pdf.PdfDocument())
            {
                var p1 = doc.AddPage();
                p1.Width = PdfSharp.Drawing.XUnit.FromPoint(612);
                p1.Height = PdfSharp.Drawing.XUnit.FromPoint(792);
                var p2 = doc.AddPage();
                p2.Width = PdfSharp.Drawing.XUnit.FromPoint(612);
                p2.Height = PdfSharp.Drawing.XUnit.FromPoint(792);
                doc.Save(testPdf);
            }

            var window = new MainWindow();
            var classifier = new FileClassifier();
            var classified = classifier.Classify(testPdf);

            var filesField = typeof(MainWindow).GetField("_files", flags)!;
            var files = (System.Collections.ObjectModel.ObservableCollection<QueuedFileItem>)filesField.GetValue(window)!;
            var queuedItem = new QueuedFileItem(classified);
            files.Add(queuedItem);

            var fileList = (ListBox)window.FindName("FileList");
            fileList.SelectedItem = queuedItem;

            var radialItemsField = typeof(MainWindow).GetField("_radialItems", flags)!;
            var radialItems = (System.Collections.ObjectModel.ObservableCollection<RadialActionItem>)radialItemsField.GetValue(window)!;
            var previewRadial = radialItems.FirstOrDefault(r => r.ActionId == ActionIds.PreviewPdf);
            Assert.NotNull(previewRadial);
            Assert.True(previewRadial.IsAvailable);

            var selectRadialMethod = typeof(MainWindow).GetMethod("SelectRadialAction", flags)!;
            selectRadialMethod.Invoke(window, [previewRadial]);

            // OpenPreviewAsync should show host and collapse radial canvas
            var pdfPreviewHost = (Grid)window.FindName("PdfPreviewHost");
            var radialCanvas = (Canvas)window.FindName("RadialMenuCanvas");
            Assert.Equal(Visibility.Visible, pdfPreviewHost.Visibility);
            Assert.Equal(Visibility.Collapsed, radialCanvas.Visibility);

            var optionsView = (StackPanel)window.FindName("PdfPreviewOptionsView");
            Assert.Equal(Visibility.Visible, optionsView.Visibility);

            // Close preview returns to radial canvas
            window.ClosePdfPreview();
            Assert.Equal(Visibility.Collapsed, pdfPreviewHost.Visibility);
            Assert.Equal(Visibility.Visible, radialCanvas.Visibility);

            window.Close();
            try { File.Delete(testPdf); Directory.Delete(tempDir, true); } catch { }
        });
    }

    [Fact]
    public void PdfPreview_EncryptedPdf_DisplaysErrorOverlayGracefully()
    {
        RunOnSta(() =>
        {
            var flags = BindingFlags.NonPublic | BindingFlags.Instance;
            var tempDir = Path.Combine(Path.GetTempPath(), "preview-enc-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var testPdf = Path.Combine(tempDir, "encrypted.pdf");

            using (var doc = new PdfSharp.Pdf.PdfDocument())
            {
                doc.AddPage();
                doc.SecuritySettings.UserPassword = "SecretPassword!";
                doc.Save(testPdf);
            }

            var window = new MainWindow();
            var classifier = new FileClassifier();
            var classified = classifier.Classify(testPdf);

            var filesField = typeof(MainWindow).GetField("_files", flags)!;
            var files = (System.Collections.ObjectModel.ObservableCollection<QueuedFileItem>)filesField.GetValue(window)!;
            var queuedItem = new QueuedFileItem(classified);
            files.Add(queuedItem);

            var fileList = (ListBox)window.FindName("FileList");
            fileList.SelectedItem = queuedItem;

            var radialItemsField = typeof(MainWindow).GetField("_radialItems", flags)!;
            var radialItems = (System.Collections.ObjectModel.ObservableCollection<RadialActionItem>)radialItemsField.GetValue(window)!;
            var previewRadial = radialItems.FirstOrDefault(r => r.ActionId == ActionIds.PreviewPdf);
            Assert.NotNull(previewRadial);

            var selectRadialMethod = typeof(MainWindow).GetMethod("SelectRadialAction", flags)!;
            selectRadialMethod.Invoke(window, [previewRadial]);

            // Wait synchronous task completion if needed
            var openPreviewMethod = typeof(MainWindow).GetMethod("OpenPdfPreviewAsync", BindingFlags.Public | BindingFlags.Instance)!;
            var task = (Task)openPreviewMethod.Invoke(window, null)!;
            WaitForTask(task);

            var errorOverlay = (Border)window.FindName("PdfPreviewErrorOverlay");
            Assert.Equal(Visibility.Visible, errorOverlay.Visibility);

            var errorTitle = (TextBlock)window.FindName("PdfPreviewErrorTitle");
            Assert.Contains("Password", errorTitle.Text, StringComparison.OrdinalIgnoreCase);

            window.ClosePdfPreview();
            Assert.Equal(Visibility.Collapsed, errorOverlay.Visibility);

            window.Close();
            try { File.Delete(testPdf); Directory.Delete(tempDir, true); } catch { }
        });
    }

    [Fact]
    public void WindowStartup_InitializesWithSidebarAndGlassmorphicTheme()
    {
        RunOnSta(() =>
        {
            var window = new MainWindow();
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;

            Assert.NotNull(window);
            Assert.Equal(980, window.MinWidth);
            Assert.Equal(640, window.MinHeight);

            // Empty state is initially visible
            var emptyState = (Border)typeof(MainWindow).GetField("EmptyState", flags)!.GetValue(window)!;
            var fileList = (ListBox)typeof(MainWindow).GetField("FileList", flags)!.GetValue(window)!;
            var queueCountText = (TextBlock)typeof(MainWindow).GetField("QueueCountText", flags)!.GetValue(window)!;

            Assert.Equal(Visibility.Visible, emptyState.Visibility);
            Assert.Equal(Visibility.Collapsed, fileList.Visibility);
            Assert.Equal("0 files", queueCountText.Text);

            // Sidebar buttons exist
            var navWorkspaceBtn = (Button)typeof(MainWindow).GetField("NavWorkspaceBtn", flags)!.GetValue(window)!;
            var navQueueBtn = (Button)typeof(MainWindow).GetField("NavQueueBtn", flags)!.GetValue(window)!;
            var navRecentBtn = (Button)typeof(MainWindow).GetField("NavRecentBtn", flags)!.GetValue(window)!;
            var navSettingsBtn = (Button)typeof(MainWindow).GetField("NavSettingsBtn", flags)!.GetValue(window)!;

            Assert.NotNull(navWorkspaceBtn);
            Assert.NotNull(navQueueBtn);
            Assert.NotNull(navRecentBtn);
            Assert.NotNull(navSettingsBtn);

            window.Close();
        });
    }

    [Fact]
    public void FileTypeIconMapping_ResolvesAppropriateGeometriesAndCategories()
    {
        RunOnSta(() =>
        {
            var classifier = new FileClassifier();

            // Create temporary test files with different extensions
            var tempDir = Path.Combine(Path.GetTempPath(), "smartshift-icon-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                var docxPath = Path.Combine(tempDir, "document.docx");
                var pptxPath = Path.Combine(tempDir, "slides.pptx");
                var xlsxPath = Path.Combine(tempDir, "sheet.xlsx");
                var mp4Path = Path.Combine(tempDir, "video.mp4");
                var txtPath = Path.Combine(tempDir, "notes.txt");
                var unknownPath = Path.Combine(tempDir, "archive.bin");

                File.WriteAllBytes(docxPath, new byte[] { 1, 2, 3 });
                File.WriteAllBytes(pptxPath, new byte[] { 1, 2, 3 });
                File.WriteAllBytes(xlsxPath, new byte[] { 1, 2, 3 });
                File.WriteAllBytes(mp4Path, new byte[] { 1, 2, 3 });
                File.WriteAllBytes(txtPath, new byte[] { 1, 2, 3 });
                File.WriteAllBytes(unknownPath, new byte[] { 1, 2, 3 });

                var itemDocx = new QueuedFileItem(classifier.Classify(docxPath));
                var itemPptx = new QueuedFileItem(classifier.Classify(pptxPath));
                var itemXlsx = new QueuedFileItem(classifier.Classify(xlsxPath));
                var itemMp4 = new QueuedFileItem(classifier.Classify(mp4Path));
                var itemTxt = new QueuedFileItem(classifier.Classify(txtPath));
                var itemUnknown = new QueuedFileItem(classifier.Classify(unknownPath));

                Assert.Equal("Word Document", itemDocx.CategoryName);
                Assert.Equal("IconWordGeometry", itemDocx.CategoryIconKey);

                Assert.Equal("PowerPoint", itemPptx.CategoryName);
                Assert.Equal("IconPptxGeometry", itemPptx.CategoryIconKey);

                Assert.Equal("Spreadsheet", itemXlsx.CategoryName);
                Assert.Equal("IconExcelGeometry", itemXlsx.CategoryIconKey);

                Assert.Equal("Video", itemMp4.CategoryName);
                Assert.Equal("IconVideoGeometry", itemMp4.CategoryIconKey);

                Assert.Equal("Text Document", itemTxt.CategoryName);
                Assert.Equal("IconTextGeometry", itemTxt.CategoryIconKey);

                Assert.Equal("File", itemUnknown.CategoryName);
                Assert.Equal("IconFileUnknownGeometry", itemUnknown.CategoryIconKey);
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        });
    }

    [Fact]
    public void PdfThumbnailGeneration_RendersRealFirstPageThumbnail()
    {
        RunOnSta(() =>
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "smartshift-thumb-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var testPdf = Path.Combine(tempDir, "thumb.pdf");

            try
            {
                using (var doc = new PdfSharp.Pdf.PdfDocument())
                {
                    var page = doc.AddPage();
                    using var gfx = PdfSharp.Drawing.XGraphics.FromPdfPage(page);
                    gfx.DrawString("PDF Thumbnail Test", new PdfSharp.Drawing.XFont("Arial", 14, PdfSharp.Drawing.XFontStyleEx.Bold), PdfSharp.Drawing.XBrushes.Navy, new PdfSharp.Drawing.XPoint(50, 50));
                    doc.Save(testPdf);
                }

                var classifier = new FileClassifier();
                var classified = classifier.Classify(testPdf);
                var item = new QueuedFileItem(classified);

                Assert.Equal("PDF Document", item.CategoryName);
                Assert.Equal("IconPdfGeometry", item.CategoryIconKey);

                // Wait for asynchronous thumbnail generation
                for (int i = 0; i < 40; i++)
                {
                    var frame = new System.Windows.Threading.DispatcherFrame();
                    Task.Delay(50).ContinueWith(_ => frame.Continue = false);
                    System.Windows.Threading.Dispatcher.PushFrame(frame);

                    if (item.HasThumbnail && item.Thumbnail is not null)
                    {
                        break;
                    }
                }

                Assert.True(item.HasThumbnail);
                Assert.NotNull(item.Thumbnail);
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        });
    }

    [Fact]
    public void SidebarNavigation_TogglesViewsAndFlyouts()
    {
        RunOnSta(() =>
        {
            var window = new MainWindow();
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;

            var recentFlyout = (Border)typeof(MainWindow).GetField("RecentResultsFlyout", flags)!.GetValue(window)!;
            var settingsFlyout = (Border)typeof(MainWindow).GetField("SettingsFlyout", flags)!.GetValue(window)!;
            var sidebarColDef = (ColumnDefinition)typeof(MainWindow).GetField("SidebarColDef", flags)!.GetValue(window)!;

            // Initially flyouts are collapsed
            Assert.Equal(Visibility.Collapsed, recentFlyout.Visibility);
            Assert.Equal(Visibility.Collapsed, settingsFlyout.Visibility);
            Assert.Equal(64, sidebarColDef.Width.Value);

            // Toggle Recent Flyout
            var navRecentMethod = typeof(MainWindow).GetMethod("NavRecent_Click", flags)!;
            navRecentMethod.Invoke(window, new object[] { window, new RoutedEventArgs() });
            Assert.Equal(Visibility.Visible, recentFlyout.Visibility);

            navRecentMethod.Invoke(window, new object[] { window, new RoutedEventArgs() });
            Assert.Equal(Visibility.Collapsed, recentFlyout.Visibility);

            // Toggle Settings Flyout
            var navSettingsMethod = typeof(MainWindow).GetMethod("NavSettings_Click", flags)!;
            navSettingsMethod.Invoke(window, new object[] { window, new RoutedEventArgs() });
            Assert.Equal(Visibility.Visible, settingsFlyout.Visibility);

            // Toggle Sidebar Expand / Collapse
            var toggleSidebarMethod = typeof(MainWindow).GetMethod("ToggleSidebar_Click", flags)!;
            toggleSidebarMethod.Invoke(window, new object[] { window, new RoutedEventArgs() });
            Assert.Equal(180, sidebarColDef.Width.Value);

            toggleSidebarMethod.Invoke(window, new object[] { window, new RoutedEventArgs() });
            Assert.Equal(64, sidebarColDef.Width.Value);

            window.Close();
        });
    }

    [Fact]
    public void WindowResizing_AdaptsDimensionsAndMaintainsSensibleMinimums()
    {
        RunOnSta(() =>
        {
            var window = new MainWindow();

            // Verify min dimensions
            Assert.True(window.MinWidth >= 900);
            Assert.True(window.MinHeight >= 600);

            // Test layout at 1280x800
            window.Width = 1280;
            window.Height = 800;
            window.Measure(new Size(1280, 800));
            window.Arrange(new Rect(0, 0, 1280, 800));

            Assert.True(window.ActualWidth <= 1280);
            Assert.True(window.ActualHeight <= 800);

            // Test layout at 1920x1080
            window.Width = 1920;
            window.Height = 1080;
            window.Measure(new Size(1920, 1080));
            window.Arrange(new Rect(0, 0, 1920, 1080));

            Assert.True(window.ActualWidth <= 1920);
            Assert.True(window.ActualHeight <= 1080);

            window.Close();
        });
    }

    [Fact]
    public void MainWindow_ImageEditorControls_InitializeCorrectly()
    {
        RunOnSta(() =>
        {
            var window = new MainWindow();
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;

            var editorHost = (Grid)typeof(MainWindow).GetField("ImageEditorHost", flags)!.GetValue(window)!;
            var heroEditBtn = (Button)typeof(MainWindow).GetField("HeroEditImageBtn", flags)!.GetValue(window)!;
            var applyCropBtn = (Button)typeof(MainWindow).GetField("EditorApplyCropBtn", flags)!.GetValue(window)!;
            var rotateCwBtn = (Button)typeof(MainWindow).GetField("EditorRotateCwBtn", flags)!.GetValue(window)!;
            var rotateCcwBtn = (Button)typeof(MainWindow).GetField("EditorRotateCcwBtn", flags)!.GetValue(window)!;
            var flipHBtn = (Button)typeof(MainWindow).GetField("EditorFlipHBtn", flags)!.GetValue(window)!;
            var flipVBtn = (Button)typeof(MainWindow).GetField("EditorFlipVBtn", flags)!.GetValue(window)!;
            var resizeWidthBox = (TextBox)typeof(MainWindow).GetField("EditorResizeWidthBox", flags)!.GetValue(window)!;
            var resizeHeightBox = (TextBox)typeof(MainWindow).GetField("EditorResizeHeightBox", flags)!.GetValue(window)!;
            var lockAspectCheck = (CheckBox)typeof(MainWindow).GetField("EditorResizeLockAspectCheck", flags)!.GetValue(window)!;
            var applyResizeBtn = (Button)typeof(MainWindow).GetField("EditorApplyResizeBtn", flags)!.GetValue(window)!;
            var viewportCanvas = (Canvas)typeof(MainWindow).GetField("EditorViewportCanvas", flags)!.GetValue(window)!;

            Assert.NotNull(editorHost);
            Assert.NotNull(heroEditBtn);
            Assert.NotNull(applyCropBtn);
            Assert.NotNull(rotateCwBtn);
            Assert.NotNull(rotateCcwBtn);
            Assert.NotNull(flipHBtn);
            Assert.NotNull(flipVBtn);
            Assert.NotNull(resizeWidthBox);
            Assert.NotNull(resizeHeightBox);
            Assert.NotNull(lockAspectCheck);
            Assert.NotNull(applyResizeBtn);
            Assert.NotNull(viewportCanvas);

            Assert.Equal(Visibility.Collapsed, editorHost.Visibility);
            Assert.Equal(Visibility.Collapsed, heroEditBtn.Visibility);
            Assert.True(lockAspectCheck.IsChecked);

            window.Close();
        });
    }

    [Fact]
    public void MainWindow_OpenAndCloseImageEditor_TogglesVisibility()
    {
        RunOnSta(() =>
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "smartshift-ui-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var testPng = Path.Combine(tempDir, "sample.png");

            using (var bitmap = new SKBitmap(100, 100))
            {
                bitmap.Erase(SKColors.Red);
                using var fs = File.Create(testPng);
                bitmap.Encode(fs, SKEncodedImageFormat.Png, 100);
            }

            var window = new MainWindow();
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;

            var editorHost = (Grid)typeof(MainWindow).GetField("ImageEditorHost", flags)!.GetValue(window)!;
            var radialCanvas = (Canvas)typeof(MainWindow).GetField("RadialMenuCanvas", flags)!.GetValue(window)!;

            var classified = new FileClassifier().Classify(testPng);

            // Open Image Editor directly
            window.OpenImageEditor(classified);

            Assert.Equal(Visibility.Visible, editorHost.Visibility);
            Assert.Equal(Visibility.Collapsed, radialCanvas.Visibility);

            // Close Image Editor
            window.CloseImageEditor();

            Assert.Equal(Visibility.Collapsed, editorHost.Visibility);
            Assert.Equal(Visibility.Visible, radialCanvas.Visibility);

            window.Close();

            try { File.Delete(testPng); Directory.Delete(tempDir, true); } catch { }
        });
    }

    [Fact]
    public void MainWindow_EditorResizeValidation_RejectsNegativeOrMalformed()
    {
        RunOnSta(() =>
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "smartshift-ui-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var testPng = Path.Combine(tempDir, "sample.png");

            using (var bitmap = new SKBitmap(200, 200))
            {
                bitmap.Erase(SKColors.Blue);
                using var fs = File.Create(testPng);
                bitmap.Encode(fs, SKEncodedImageFormat.Png, 100);
            }

            var window = new MainWindow();
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;

            var classified = new FileClassifier().Classify(testPng);
            window.OpenImageEditor(classified);

            var wBox = (TextBox)typeof(MainWindow).GetField("EditorResizeWidthBox", flags)!.GetValue(window)!;
            var hBox = (TextBox)typeof(MainWindow).GetField("EditorResizeHeightBox", flags)!.GetValue(window)!;
            var applyBtn = (Button)typeof(MainWindow).GetField("EditorApplyResizeBtn", flags)!.GetValue(window)!;
            var validationText = (TextBlock)typeof(MainWindow).GetField("EditorResizeValidationText", flags)!.GetValue(window)!;

            // Malformed input
            wBox.Text = "abc";
            applyBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(Visibility.Visible, validationText.Visibility);
            Assert.Contains("positive", validationText.Text, StringComparison.OrdinalIgnoreCase);

            // Negative input
            wBox.Text = "-50";
            applyBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(Visibility.Visible, validationText.Visibility);

            // Oversized input exceeding ImageSafetyLimits (8192)
            wBox.Text = "99999";
            hBox.Text = "99999";
            applyBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(Visibility.Visible, validationText.Visibility);
            Assert.Contains("safety", validationText.Text, StringComparison.OrdinalIgnoreCase);

            window.CloseImageEditor();
            window.Close();

            try { File.Delete(testPng); Directory.Delete(tempDir, true); } catch { }
        });
    }

    [Fact]
    public void MainWindow_BatchStudio_FieldsExist_AndAreInitialized()
    {
        RunOnSta(() =>
        {
            var window = new MainWindow();
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;

            var batchHost = (Grid)typeof(MainWindow).GetField("BatchStudioHost", flags)!.GetValue(window)!;
            var batchFileList = (ListBox)typeof(MainWindow).GetField("BatchFileList", flags)!.GetValue(window)!;
            var startBtn = (Button)typeof(MainWindow).GetField("BatchStartButton", flags)!.GetValue(window)!;
            var cancelBtn = (Button)typeof(MainWindow).GetField("BatchCancelButton", flags)!.GetValue(window)!;
            var progressCard = (Border)typeof(MainWindow).GetField("BatchProgressCard", flags)!.GetValue(window)!;
            var summaryCard = (Border)typeof(MainWindow).GetField("BatchSummaryCard", flags)!.GetValue(window)!;
            var inspectorCard = (Border)typeof(MainWindow).GetField("BatchSelectedInspectorCard", flags)!.GetValue(window)!;
            var shelfBatchBtn = (Button)typeof(MainWindow).GetField("ShelfBatchButton", flags)!.GetValue(window)!;

            Assert.NotNull(batchHost);
            Assert.NotNull(batchFileList);
            Assert.NotNull(startBtn);
            Assert.NotNull(cancelBtn);
            Assert.NotNull(progressCard);
            Assert.NotNull(summaryCard);
            Assert.NotNull(inspectorCard);
            Assert.NotNull(shelfBatchBtn);

            Assert.Equal(Visibility.Collapsed, batchHost.Visibility);
            Assert.Equal(Visibility.Collapsed, shelfBatchBtn.Visibility);
            Assert.False(cancelBtn.IsEnabled);

            window.Close();
        });
    }

    [Fact]
    public void MainWindow_OpenAndCloseBatchStudio_TogglesVisibility()
    {
        RunOnSta(() =>
        {
            var window = new MainWindow();
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;

            var batchHost = (Grid)typeof(MainWindow).GetField("BatchStudioHost", flags)!.GetValue(window)!;
            var navQueueBtn = (Button)typeof(MainWindow).GetField("NavQueueBtn", flags)!.GetValue(window)!;
            var navWorkspaceBtn = (Button)typeof(MainWindow).GetField("NavWorkspaceBtn", flags)!.GetValue(window)!;

            // Open batch studio
            window.OpenBatchStudio();
            Assert.Equal(Visibility.Visible, batchHost.Visibility);
            Assert.Equal("Active", navQueueBtn.Tag);

            // Close batch studio
            window.CloseBatchStudio();
            Assert.Equal(Visibility.Collapsed, batchHost.Visibility);
            Assert.Equal("Active", navWorkspaceBtn.Tag);

            window.Close();
        });
    }

    [Fact]
    public void MainWindow_BatchStudio_OperationSelection_TogglesPanels()
    {
        RunOnSta(() =>
        {
            var window = new MainWindow();
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;

            window.OpenBatchStudio();

            var compressPanel = (StackPanel)typeof(MainWindow).GetField("BatchCompressSettingsPanel", flags)!.GetValue(window)!;
            var convertPanel = (StackPanel)typeof(MainWindow).GetField("BatchConvertSettingsPanel", flags)!.GetValue(window)!;
            var resizePanel = (StackPanel)typeof(MainWindow).GetField("BatchResizeSettingsPanel", flags)!.GetValue(window)!;
            var metaPanel = (StackPanel)typeof(MainWindow).GetField("BatchMetadataSettingsPanel", flags)!.GetValue(window)!;

            var opConvertBtn = (Button)typeof(MainWindow).GetField("BatchOpConvertBtn", flags)!.GetValue(window)!;
            var opResizeBtn = (Button)typeof(MainWindow).GetField("BatchOpResizeBtn", flags)!.GetValue(window)!;
            var opMetaBtn = (Button)typeof(MainWindow).GetField("BatchOpMetadataBtn", flags)!.GetValue(window)!;
            var opCompressBtn = (Button)typeof(MainWindow).GetField("BatchOpCompressBtn", flags)!.GetValue(window)!;

            // Switch to Convert
            opConvertBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(Visibility.Visible, convertPanel.Visibility);
            Assert.Equal(Visibility.Collapsed, compressPanel.Visibility);

            // Switch to Resize
            opResizeBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(Visibility.Visible, resizePanel.Visibility);
            Assert.Equal(Visibility.Collapsed, convertPanel.Visibility);

            // Switch to Metadata
            opMetaBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(Visibility.Visible, metaPanel.Visibility);
            Assert.Equal(Visibility.Collapsed, resizePanel.Visibility);

            // Switch back to Compress
            opCompressBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(Visibility.Visible, compressPanel.Visibility);
            Assert.Equal(Visibility.Collapsed, metaPanel.Visibility);

            window.CloseBatchStudio();
            window.Close();
        });
    }
}
