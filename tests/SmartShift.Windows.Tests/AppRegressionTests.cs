using System;
using System.Linq;
using System.Windows;
using SmartShift.App;
using SmartShift.Core.Logging;
using SmartShift.Processing.PresentationStudio.Models;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace SmartShift.Windows.Tests;

public sealed class AppRegressionTests
{
    [Fact]
    public void AppServices_DiAndLifecycle_WiredCorrectly()
    {
        var logger = new DiagnosticLogger();
        using var services = new AppServices(logger);

        Assert.NotNull(services.Logger);
        Assert.NotNull(services.Classifier);
        Assert.NotNull(services.Queue);
        Assert.NotNull(services.Recommendations);
        Assert.NotNull(services.Processors);
        Assert.NotNull(services.BatchEngine);
        Assert.NotNull(services.DocumentBuilder);
        Assert.NotNull(services.PresentationStudio);
        Assert.NotNull(services.PdfSplit);
        Assert.NotNull(services.Outputs);
        Assert.NotNull(services.LogSink);

        // Verify PresentationStudio is wired with outputs and default catalog
        Assert.NotNull(services.PresentationStudio.Importers);
        Assert.NotNull(services.PresentationStudio.OutlineGenerator);
        Assert.NotNull(services.PresentationStudio.AiService);
        Assert.NotNull(services.PresentationStudio.Exporter);
    }

    [Fact]
    public void Step41Removal_ReflectionAudit_ZeroStep41RemnantsInAppAssembly()
    {
        var appAssembly = typeof(AppServices).Assembly;
        var allTypes = appAssembly.GetTypes().ToList();

        // 1. Planning, Classifier, & Web Image types must not exist in SmartShift.App
        Assert.DoesNotContain(allTypes, t => t.Name.Contains("ProcessStepPreviewItem", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(allTypes, t => t.Name.Contains("KeyMetricPreviewItem", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(allTypes, t => t.Name.Contains("PresentationPlanner", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(allTypes, t => t.Name.Contains("ImageSearch", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(allTypes, t => t.Name.Contains("Pexels", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(allTypes, t => t.Name.Contains("Wikimedia", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MainWindow_PresentationStudio_HasOriginalLayoutsAndNoStep41Controls()
    {
        Exception? threadEx = null;
        var runThread = new System.Threading.Thread(() =>
        {
            try
            {
                var app = Application.Current ?? new Application();
                var themeUri = new Uri("pack://application:,,,/SmartShift.App;component/Themes/AppTheme.xaml", UriKind.Absolute);
                if (!app.Resources.MergedDictionaries.Any(d => d.Source == themeUri))
                {
                    app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = themeUri });
                }

                if (SmartShift.App.App.Services is null)
                {
                    var prop = typeof(SmartShift.App.App).GetProperty("Services", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    prop?.SetValue(null, new AppServices(SmartShift.App.App.Logger));
                }

                var window = new MainWindow();
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;

                // Verify Step 4.1 controls are completely absent from window fields
                Assert.Null(typeof(MainWindow).GetField("PresIntelligentPlanningCheck", flags));
                Assert.Null(typeof(MainWindow).GetField("PresWebImagesCheck", flags));
                Assert.Null(typeof(MainWindow).GetField("PresSearchWebImageBtn", flags));
                Assert.Null(typeof(MainWindow).GetField("PresLayoutProcessBtn", flags));
                Assert.Null(typeof(MainWindow).GetField("PresLayoutMetricsBtn", flags));
                Assert.Null(typeof(MainWindow).GetField("PresLayoutCompareBtn", flags));
                Assert.Null(typeof(MainWindow).GetField("PresLayoutQuoteBtn", flags));
                Assert.Null(typeof(MainWindow).GetField("PresSlidePreviewProcessContainer", flags));
                Assert.Null(typeof(MainWindow).GetField("PresSlidePreviewMetricsContainer", flags));
                Assert.Null(typeof(MainWindow).GetField("PresSlidePreviewComparisonContainer", flags));
                Assert.Null(typeof(MainWindow).GetField("PresSlidePreviewQuoteContainer", flags));

                // Verify Step 4 original controls ARE present
                Assert.NotNull(typeof(MainWindow).GetField("PresLayoutTitleBtn", flags));
                Assert.NotNull(typeof(MainWindow).GetField("PresLayoutContentBtn", flags));
                Assert.NotNull(typeof(MainWindow).GetField("PresLayoutDividerBtn", flags));
                Assert.NotNull(typeof(MainWindow).GetField("PresLayoutTwoColBtn", flags));
                Assert.NotNull(typeof(MainWindow).GetField("PresLayoutTextImgBtn", flags));
                Assert.NotNull(typeof(MainWindow).GetField("PresLayoutImgFocusBtn", flags));
                Assert.NotNull(typeof(MainWindow).GetField("PresLayoutBlankBtn", flags));

                window.Close();
            }
            catch (Exception ex)
            {
                threadEx = ex;
            }
        });

        runThread.SetApartmentState(System.Threading.ApartmentState.STA);
        runThread.Start();
        runThread.Join();

        if (threadEx is not null)
        {
            throw new AggregateException("STA thread execution failed", threadEx);
        }
    }
}
