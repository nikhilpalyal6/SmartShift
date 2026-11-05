using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using SmartShift.App;
using SmartShift.App.Ui;
using SmartShift.Core.Actions;
using Xunit;
using AppClass = SmartShift.App.App;

namespace SmartShift.Windows.Tests;

public sealed class PdfSplitUiTests
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
                    new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

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

    private static void WaitForTask(Task task)
    {
        var frame = new DispatcherFrame();
        task.ContinueWith(_ => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    private static string CreateTestPdf(int pageCount)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"smartshift_split_ui_test_{Guid.NewGuid():N}.pdf");
        using var doc = new PdfDocument();
        for (int i = 1; i <= pageCount; i++)
        {
            var page = doc.AddPage();
            page.Width = XUnit.FromPoint(400);
            page.Height = XUnit.FromPoint(300);
            using var gfx = XGraphics.FromPdfPage(page);
            gfx.DrawString($"Page {i}", new XFont("Arial", 16, XFontStyleEx.Regular), XBrushes.Black, new XPoint(50, 50));
        }
        doc.Save(tempFile);
        return tempFile;
    }

    [Fact]
    public void PdfSplitStudio_OpenAndClose_TogglesVisibilityAndNavTag()
    {
        RunOnSta(() =>
        {
            var window = new MainWindow();
            try
            {
                var splitHost = (Grid)window.FindName("PdfSplitStudioHost");
                var navBtn = (Button)window.FindName("NavPdfSplitBtn");

                Assert.Equal(Visibility.Collapsed, splitHost.Visibility);

                window.OpenPdfSplitStudio();
                Assert.Equal(Visibility.Visible, splitHost.Visibility);
                Assert.Equal("Active", navBtn.Tag);

                window.ClosePdfSplitStudio();
                Assert.Equal(Visibility.Collapsed, splitHost.Visibility);
                Assert.Null(navBtn.Tag);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void PdfSplitStudio_LoadValidPdf_PopulatesUiAndSelectsAll()
    {
        var pdfPath = CreateTestPdf(3);
        try
        {
            RunOnSta(() =>
            {
                var window = new MainWindow();
                try
                {
                    var splitHost = (Grid)window.FindName("PdfSplitStudioHost");
                    var fileNameText = (TextBlock)window.FindName("PdfSplitHeaderFileName");
                    var pageCountText = (TextBlock)window.FindName("PdfSplitHeaderPageCount");
                    var rangeBox = (TextBox)window.FindName("PdfSplitRangeBox");
                    var execBtn = (Button)window.FindName("PdfSplitExecuteBtn");
                    var selectBadge = (TextBlock)window.FindName("PdfSplitSelectionCountText");

                    window.OpenPdfSplitStudio();
                    WaitForTask(window.LoadPdfSplitDocumentAsync(pdfPath));

                    Assert.Equal(Visibility.Visible, splitHost.Visibility);
                    Assert.Equal(Path.GetFileName(pdfPath), fileNameText.Text);
                    Assert.Equal("3 pages", pageCountText.Text);
                    Assert.Equal("1-3", rangeBox.Text);
                    Assert.Equal("3 of 3 pages selected", selectBadge.Text);
                    Assert.True(execBtn.IsEnabled);
                }
                finally
                {
                    window.Close();
                }
            });
        }
        finally
        {
            try { File.Delete(pdfPath); } catch { }
        }
    }

    [Fact]
    public void PdfSplitStudio_ModeSwitching_UpdatesPanelsAndOutputs()
    {
        var pdfPath = CreateTestPdf(4);
        try
        {
            RunOnSta(() =>
            {
                var window = new MainWindow();
                try
                {
                    window.OpenPdfSplitStudio();
                    WaitForTask(window.LoadPdfSplitDocumentAsync(pdfPath));

                    var extractRadio = (RadioButton)window.FindName("PdfSplitModeExtractRadio");
                    var everyRadio = (RadioButton)window.FindName("PdfSplitModeEveryRadio");
                    var extractPanel = (StackPanel)window.FindName("PdfSplitModeExtractPanel");
                    var everyPanel = (StackPanel)window.FindName("PdfSplitModeEveryPanel");
                    var execText = (TextBlock)window.FindName("PdfSplitExecuteBtnText");
                    var plannedList = (ItemsControl)window.FindName("PdfSplitPlannedOutputsList");

                    // Initial state: Extract Selected Ranges
                    Assert.True(extractRadio.IsChecked);
                    Assert.Equal(Visibility.Visible, extractPanel.Visibility);
                    Assert.Equal(Visibility.Collapsed, everyPanel.Visibility);
                    Assert.Equal("Extract Selected Ranges", execText.Text);

                    // Switch to Split Every Page
                    everyRadio.IsChecked = true;
                    Assert.Equal(Visibility.Collapsed, extractPanel.Visibility);
                    Assert.Equal(Visibility.Visible, everyPanel.Visibility);
                    Assert.Equal("Split Every Page", execText.Text);

                    // 4 output items planned
                    var plannedOutputs = (System.Collections.IEnumerable)plannedList.ItemsSource;
                    int count = 0;
                    foreach (var _ in plannedOutputs) count++;
                    Assert.Equal(4, count);

                    // Switch back to Extract Ranges
                    extractRadio.IsChecked = true;
                    Assert.Equal(Visibility.Visible, extractPanel.Visibility);
                    Assert.Equal(Visibility.Collapsed, everyPanel.Visibility);
                    Assert.Equal("Extract Selected Ranges", execText.Text);
                }
                finally
                {
                    window.Close();
                }
            });
        }
        finally
        {
            try { File.Delete(pdfPath); } catch { }
        }
    }

    [Fact]
    public void PdfSplitStudio_RangeValidation_RejectsOutOfBoundsOrDuplicates()
    {
        var pdfPath = CreateTestPdf(3);
        try
        {
            RunOnSta(() =>
            {
                var window = new MainWindow();
                try
                {
                    window.OpenPdfSplitStudio();
                    WaitForTask(window.LoadPdfSplitDocumentAsync(pdfPath));

                    var rangeBox = (TextBox)window.FindName("PdfSplitRangeBox");
                    var validationText = (TextBlock)window.FindName("PdfSplitRangeValidationText");
                    var execBtn = (Button)window.FindName("PdfSplitExecuteBtn");

                    // Set out-of-bounds range
                    rangeBox.Text = "1-5";
                    Assert.Equal(Visibility.Visible, validationText.Visibility);
                    Assert.False(execBtn.IsEnabled);

                    // Set duplicate page
                    rangeBox.Text = "1, 1-2";
                    Assert.Equal(Visibility.Visible, validationText.Visibility);
                    Assert.False(execBtn.IsEnabled);

                    // Set valid range
                    rangeBox.Text = "1, 3";
                    Assert.Equal(Visibility.Collapsed, validationText.Visibility);
                    Assert.True(execBtn.IsEnabled);
                }
                finally
                {
                    window.Close();
                }
            });
        }
        finally
        {
            try { File.Delete(pdfPath); } catch { }
        }
    }
}
