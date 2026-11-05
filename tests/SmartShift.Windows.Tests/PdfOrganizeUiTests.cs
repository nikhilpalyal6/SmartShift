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

public sealed class PdfOrganizeUiTests
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
        var tempFile = Path.Combine(Path.GetTempPath(), $"smartshift_organize_ui_test_{Guid.NewGuid():N}.pdf");
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
    public void PdfOrganizeStudio_OpenAndClose_TogglesVisibilityAndNavTag()
    {
        RunOnSta(() =>
        {
            var window = new MainWindow();
            try
            {
                var organizeHost = (Grid)window.FindName("PdfOrganizeStudioHost");
                var navBtn = (Button)window.FindName("NavPdfOrganizeBtn");

                Assert.Equal(Visibility.Collapsed, organizeHost.Visibility);

                window.OpenPdfOrganizeStudio();
                Assert.Equal(Visibility.Visible, organizeHost.Visibility);
                Assert.Equal("Active", navBtn.Tag);

                window.ClosePdfOrganizeStudio();
                Assert.Equal(Visibility.Collapsed, organizeHost.Visibility);
                Assert.Null(navBtn.Tag);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void PdfOrganizeStudio_LoadsDocument_PopulatesHeaderAndPages()
    {
        RunOnSta(() =>
        {
            var testPdf = CreateTestPdf(4);
            var window = new MainWindow();
            try
            {
                var headerFileName = (TextBlock)window.FindName("PdfOrganizeHeaderFileName");
                var headerPageCount = (TextBlock)window.FindName("PdfOrganizeHeaderPageCount");
                var outputNameBox = (TextBox)window.FindName("PdfOrganizeOutputFileNameBox");

                WaitForTask(window.LoadPdfOrganizeDocumentAsync(testPdf));

                Assert.Equal(Path.GetFileName(testPdf), headerFileName.Text);
                Assert.Equal("4 pages", headerPageCount.Text);
                Assert.Contains("_organized.pdf", outputNameBox.Text);
            }
            finally
            {
                window.Close();
                try { File.Delete(testPdf); } catch { }
            }
        });
    }

    [Fact]
    public void PdfOrganizeStudio_MutualSwitching_ClosesOtherStudios()
    {
        RunOnSta(() =>
        {
            var window = new MainWindow();
            try
            {
                var splitHost = (Grid)window.FindName("PdfSplitStudioHost");
                var mergeHost = (Grid)window.FindName("PdfMergeStudioHost");
                var organizeHost = (Grid)window.FindName("PdfOrganizeStudioHost");

                // Open split studio
                window.OpenPdfSplitStudio();
                Assert.Equal(Visibility.Visible, splitHost.Visibility);
                Assert.Equal(Visibility.Collapsed, organizeHost.Visibility);

                // Open organize studio -> should close split studio
                window.OpenPdfOrganizeStudio();
                Assert.Equal(Visibility.Visible, organizeHost.Visibility);
                Assert.Equal(Visibility.Collapsed, splitHost.Visibility);

                // Open merge studio -> should close organize studio
                window.OpenPdfMergeStudio();
                Assert.Equal(Visibility.Visible, mergeHost.Visibility);
                Assert.Equal(Visibility.Collapsed, organizeHost.Visibility);
            }
            finally
            {
                window.Close();
            }
        });
    }

    // =========================================================================
    // SESSION MODEL UNIT TESTS
    // =========================================================================

    [Fact]
    public void Session_Initialize_SetsOriginalState()
    {
        var session = new PdfPageOrganizerSession();
        session.Initialize(
            "test.pdf",
            5,
            1024,
            [0, 90, 0, 180, 0],
            [(500, 700), (500, 700), (500, 700), (500, 700), (500, 700)]);

        Assert.Equal(5, session.Pages.Count);
        Assert.False(session.HasUnsavedChanges);
        Assert.False(session.CanUndo);
        Assert.False(session.CanRedo);
        Assert.Equal(0, session.SelectedCount);
        Assert.True(session.CanExport);
    }

    [Fact]
    public void Session_MoveSelected_ReordersPagesDeterministically()
    {
        var session = new PdfPageOrganizerSession();
        session.Initialize(
            "test.pdf",
            4,
            1024,
            [0, 0, 0, 0],
            [(500, 700), (500, 700), (500, 700), (500, 700)]);

        // Select page 2 (index 1) and move left
        session.Pages[1].IsSelected = true;
        Assert.True(session.CanMoveLeft);

        session.MoveSelected(-1);

        // Page 2 should now be at position 0
        Assert.Equal(1, session.Pages[0].OriginalPageIndex);
        Assert.Equal(0, session.Pages[1].OriginalPageIndex);
        Assert.True(session.HasUnsavedChanges);
        Assert.True(session.CanUndo);

        // Move right back
        session.MoveSelected(1);
        Assert.Equal(0, session.Pages[0].OriginalPageIndex);
        Assert.Equal(1, session.Pages[1].OriginalPageIndex);
    }

    [Fact]
    public void Session_RotateSelected_UpdatesRotationAndEffectiveAngle()
    {
        var session = new PdfPageOrganizerSession();
        session.Initialize(
            "test.pdf",
            2,
            1024,
            [0, 90], // Page 1 has inherent 90° rotation
            [(500, 700), (500, 700)]);

        session.Pages[0].IsSelected = true;
        session.Pages[1].IsSelected = true;

        session.RotateSelected(90);

        Assert.Equal(90, session.Pages[0].RotationDegrees);
        Assert.Equal(90, session.Pages[0].EffectiveRotation);

        Assert.Equal(90, session.Pages[1].RotationDegrees);
        Assert.Equal(180, session.Pages[1].EffectiveRotation); // 90 inherent + 90 = 180

        Assert.True(session.HasUnsavedChanges);
        Assert.True(session.CanUndo);

        // Rotate again by 180
        session.RotateSelected(180);
        Assert.Equal(270, session.Pages[0].RotationDegrees);
        Assert.Equal(0, session.Pages[1].EffectiveRotation); // (90 + 270) % 360 = 0
    }

    [Fact]
    public void Session_DeleteSelected_RemovesPagesAndPreventsEmptyDoc()
    {
        var session = new PdfPageOrganizerSession();
        session.Initialize(
            "test.pdf",
            3,
            1024,
            [0, 0, 0],
            [(500, 700), (500, 700), (500, 700)]);

        // Select page 1 (idx 0)
        session.Pages[0].IsSelected = true;
        bool deleted = session.DeleteSelected();

        Assert.True(deleted);
        Assert.Equal(2, session.Pages.Count);
        Assert.Equal(1, session.Pages[0].OriginalPageIndex);
        Assert.Equal(2, session.Pages[1].OriginalPageIndex);
        Assert.True(session.HasUnsavedChanges);

        // Select remaining 2 pages and try to delete
        session.SelectAll();
        bool deletedAll = session.DeleteSelected();

        Assert.False(deletedAll); // Prevent deleting all pages!
        Assert.Equal(2, session.Pages.Count);
    }

    [Fact]
    public void Session_UndoAndRedo_RestoresExactTransformationState()
    {
        var session = new PdfPageOrganizerSession();
        session.Initialize(
            "test.pdf",
            3,
            1024,
            [0, 0, 0],
            [(500, 700), (500, 700), (500, 700)]);

        // 1. Reorder: move page 0 to index 2
        session.MovePage(0, 2);
        Assert.Equal(1, session.Pages[0].OriginalPageIndex);
        Assert.Equal(2, session.Pages[1].OriginalPageIndex);
        Assert.Equal(0, session.Pages[2].OriginalPageIndex);

        // 2. Undo
        session.Undo();
        Assert.Equal(0, session.Pages[0].OriginalPageIndex);
        Assert.Equal(1, session.Pages[1].OriginalPageIndex);
        Assert.Equal(2, session.Pages[2].OriginalPageIndex);
        Assert.False(session.CanUndo);
        Assert.True(session.CanRedo);

        // 3. Redo
        session.Redo();
        Assert.Equal(1, session.Pages[0].OriginalPageIndex);
        Assert.Equal(2, session.Pages[1].OriginalPageIndex);
        Assert.Equal(0, session.Pages[2].OriginalPageIndex);
        Assert.True(session.CanUndo);
        Assert.False(session.CanRedo);
    }

    [Fact]
    public void Session_ResetToOriginal_RestoresAllModifications()
    {
        var session = new PdfPageOrganizerSession();
        session.Initialize(
            "test.pdf",
            4,
            1024,
            [0, 0, 0, 0],
            [(500, 700), (500, 700), (500, 700), (500, 700)]);

        // Perform multiple modifications: rotate, move, delete
        session.Pages[0].IsSelected = true;
        session.RotateSelected(90);
        session.ClearSelection();
        session.MovePage(1, 3);
        session.Pages[2].IsSelected = true;
        session.DeleteSelected();

        Assert.True(session.HasUnsavedChanges);
        Assert.Equal(3, session.Pages.Count);

        // Reset
        session.ResetToOriginal();

        Assert.Equal(4, session.Pages.Count);
        Assert.False(session.HasUnsavedChanges);
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(i, session.Pages[i].OriginalPageIndex);
            Assert.Equal(0, session.Pages[i].RotationDegrees);
            Assert.False(session.Pages[i].IsSelected);
        }
    }
}
