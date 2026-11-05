using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SkiaSharp;
using SmartShift.App;
using SmartShift.App.Ui;
using SmartShift.Core.Images;
using Xunit;
using AppClass = SmartShift.App.App;

namespace SmartShift.Windows.Tests;

public sealed class BatchImageStudioUiTests : IDisposable
{
    private readonly string _tempDir;

    public BatchImageStudioUiTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "SmartShift_BatchUiTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
        }
    }

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

    private string CreateTestImage(int width = 300, int height = 200, string format = "png")
    {
        var path = Path.Combine(_tempDir, $"img_{Guid.NewGuid():N}.{format}");
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.DarkBlue);
        using var image = surface.Snapshot();
        using var data = image.Encode(format == "jpg" ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Png, 90);
        using var stream = File.OpenWrite(path);
        data.SaveTo(stream);
        return path;
    }

    [Fact]
    public void BatchStudio_Initialization_PopulatesPresetDropdown()
    {
        RunOnSta(() =>
        {
            var window = new MainWindow();
            window.OpenBatchStudio();

            var combo = (ComboBox)window.FindName("BatchPresetCombo");
            Assert.NotNull(combo);
            Assert.True(combo.Items.Count > 1);

            var items = combo.Items.OfType<ComboBoxItem>().ToList();
            Assert.Contains(items, i => i.Content.ToString()!.Contains("Web Balanced"));
            Assert.Contains(items, i => i.Content.ToString()!.Contains("Lossless Archive"));
            Assert.Contains(items, i => i.Content.ToString()!.Contains("Target 500 KB"));

            window.Close();
        });
    }

    [Fact]
    public void BatchStudio_SelectPreset_ConfiguresTargetSizeAndFormat()
    {
        RunOnSta(() =>
        {
            var window = new MainWindow();
            window.OpenBatchStudio();

            var combo = (ComboBox)window.FindName("BatchPresetCombo");
            var targetCheck = (CheckBox)window.FindName("BatchEnableTargetSizeCheck");
            var targetBox = (TextBox)window.FindName("BatchTargetSizeBox");

            // Find "Target 500 KB" preset
            var targetPresetItem = combo.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => i.Tag is ImageOptimizationPreset p && p.TargetSizeKb == 500);

            Assert.NotNull(targetPresetItem);
            combo.SelectedItem = targetPresetItem;

            Assert.True(targetCheck.IsChecked);
            Assert.Equal("500", targetBox.Text);

            window.Close();
        });
    }

    [Fact]
    public void BatchStudio_NamingTemplate_LivePreviewUpdates()
    {
        RunOnSta(() =>
        {
            var window = new MainWindow();
            window.OpenBatchStudio();

            var templateBox = (TextBox)window.FindName("BatchNamingTemplateBox");
            var previewText = (TextBlock)window.FindName("BatchNamingPreviewText");

            templateBox.Text = "{name}_export_{n:D3}.{ext}";

            Assert.Contains("_export_", previewText.Text);
            Assert.Contains("001", previewText.Text);

            window.Close();
        });
    }

    [Fact]
    public void BatchStudio_TargetSize_TogglesVisibility()
    {
        RunOnSta(() =>
        {
            var window = new MainWindow();
            window.OpenBatchStudio();

            var targetCheck = (CheckBox)window.FindName("BatchEnableTargetSizeCheck");
            var targetPanel = (StackPanel)window.FindName("BatchTargetSizeInputPanel");

            targetCheck.IsChecked = true;
            Assert.Equal(Visibility.Visible, targetPanel.Visibility);

            targetCheck.IsChecked = false;
            Assert.Equal(Visibility.Collapsed, targetPanel.Visibility);

            window.Close();
        });
    }

    [Fact]
    public void BatchStudio_Inspector_DisplaysMetadata()
    {
        RunOnSta(() =>
        {
            var imgPath = CreateTestImage(width: 400, height: 300, format: "png");
            var classified = AppClass.Services.Classifier.Classify(imgPath);

            var window = new MainWindow();
            window.OpenBatchStudio();

            var fileList = (ListBox)window.FindName("BatchFileList");
            var inspectorCard = (Border)window.FindName("BatchSelectedInspectorCard");
            var metaCard = (Border)window.FindName("BatchInspectorMetadataCard");
            var metaDims = (TextBlock)window.FindName("BatchInspectorMetaDims");
            var metaFormat = (TextBlock)window.FindName("BatchInspectorMetaFormat");

            var itemVm = new BatchQueueItemViewModel(classified);
            var batchFilesField = typeof(MainWindow).GetField("_batchFiles", BindingFlags.NonPublic | BindingFlags.Instance);
            var batchFiles = (System.Collections.ObjectModel.ObservableCollection<BatchQueueItemViewModel>)batchFilesField!.GetValue(window)!;
            batchFiles.Add(itemVm);

            fileList.SelectedItem = itemVm;

            Assert.Equal(Visibility.Visible, inspectorCard.Visibility);
            Assert.Equal(Visibility.Visible, metaCard.Visibility);
            Assert.Contains("400 × 300", metaDims.Text);
            Assert.Contains("4:3", metaDims.Text);
            Assert.Contains("Png", metaFormat.Text);

            window.Close();
        });
    }
}
