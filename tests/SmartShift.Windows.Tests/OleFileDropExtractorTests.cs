using System.IO;
using System.Linq;
using System.Windows;
using SmartShift.Core.Drops;
using SmartShift.Windows.DragDrop;
using Xunit;

namespace SmartShift.Windows.Tests;

public sealed class OleFileDropExtractorTests
{
    [Fact]
    public void Extract_FileDropDataObject_ReturnsPaths()
    {
        RunSta(() =>
        {
            var path = WriteTemp("shelf.png", "image");
            try
            {
                var data = new DataObject(DataFormats.FileDrop, new[] { path });
                var result = OleFileDropExtractor.Extract(data, new ModifierKeySnapshot(false, false, false, false, false));
                Assert.True(result.HasFileDrop);
                Assert.EndsWith("shelf.png", result.Inspection.Files[0].FileName, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(SupportedFileKind.Image, result.Inspection.Files[0].Kind);
                Assert.Equal("image", File.ReadAllText(path));
            }
            finally
            {
                File.Delete(path);
            }
        });
    }

    [Fact]
    public void Extract_MultipleFiles_PreservesOrderAndCount()
    {
        RunSta(() =>
        {
            var a = WriteTemp("one.pdf", "p");
            var b = WriteTemp("two.jpg", "j");
            try
            {
                var data = new DataObject(DataFormats.FileDrop, new[] { a, b });
                var result = OleFileDropExtractor.Extract(data);
                Assert.Equal(2, result.Inspection.FileCount);
                Assert.Equal(2, result.Inspection.FileNames.Count());
                Assert.All(result.Inspection.FileNames, name => Assert.True(
                    name.EndsWith("one.pdf", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith("two.jpg", StringComparison.OrdinalIgnoreCase)));
            }
            finally
            {
                File.Delete(a);
                File.Delete(b);
            }
        });
    }

    [Fact]
    public void Extract_EmptyAndNonFilePayloads_AreRejected()
    {
        RunSta(() =>
        {
            Assert.False(OleFileDropExtractor.Extract(null).HasFileDrop);
            Assert.False(OleFileDropExtractor.Extract(new DataObject()).HasFileDrop);
            Assert.False(OleFileDropExtractor.Extract(new DataObject(DataFormats.UnicodeText, "not-a-file")).HasFileDrop);
            Assert.False(OleFileDropExtractor.Extract(new DataObject(DataFormats.FileDrop, Array.Empty<string>())).HasFileDrop);
        });
    }

    [Fact]
    public void SafeAllowedEffects_NeverReturnsMove()
    {
        Assert.Equal(DragDropEffects.Copy, OleFileDropExtractor.SafeAllowedEffects(canInspectFiles: true));
        Assert.Equal(DragDropEffects.None, OleFileDropExtractor.SafeAllowedEffects(canInspectFiles: false));
        Assert.NotEqual(DragDropEffects.Move, OleFileDropExtractor.SafeAllowedEffects(canInspectFiles: true));
    }

    [Fact]
    public void DropShelf_ShowHideAndCleanup_OnStaThread()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var logger = new SmartShift.Core.Logging.DiagnosticLogger();
                var window = new SmartShift.Windows.Ui.DropShelfWindow(logger);
                window.Show();
                Assert.True(window.IsVisible);
                window.Close();
                Assert.False(window.IsVisible);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw failure;
        }
    }

    [Fact]
    public void ObserveOnlyMouseHook_StopWithoutStart_IsSafe()
    {
        var logger = new SmartShift.Core.Logging.DiagnosticLogger();
        using var hook = new SmartShift.Windows.Input.ObserveOnlyMouseHook(logger);
        hook.Stop();
        Assert.False(hook.IsInstalled);
    }

    [Fact]
    public void ObserveOnlyMouseHook_StartAndStop_Uninstalls()
    {
        RunSta(() =>
        {
            var logger = new SmartShift.Core.Logging.DiagnosticLogger();
            using var hook = new SmartShift.Windows.Input.ObserveOnlyMouseHook(logger);
            hook.Start();
            Assert.True(hook.IsInstalled);
            hook.Stop();
            Assert.False(hook.IsInstalled);
        });
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw failure;
        }
    }

    private static string WriteTemp(string name, string contents)
    {
        var path = Path.Combine(Path.GetTempPath(), "smartshift-win-" + Guid.NewGuid().ToString("N") + "-" + name);
        File.WriteAllText(path, contents);
        return path;
    }
}
