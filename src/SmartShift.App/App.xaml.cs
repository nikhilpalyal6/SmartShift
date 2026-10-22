using System.Windows;
using System.Windows.Threading;
using SmartShift.App.Ui;
using SmartShift.Core.Logging;

namespace SmartShift.App;

public partial class App : Application
{
    public static DiagnosticLogger Logger { get; } = new();

    public static AppServices Services { get; private set; } = null!;

    public static bool DiagnosticsRequested { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        DiagnosticsRequested = e.Args.Any(a => string.Equals(a, "--diagnostics", StringComparison.OrdinalIgnoreCase));
        ThemeLoader.Apply(this);
        Services = new AppServices(Logger);
        Logger.Info("app", "SmartShift starting.", new Dictionary<string, string>
        {
            ["diagnostics"] = DiagnosticsRequested.ToString()
        });
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Logger.Info("app", "SmartShift exiting.", new Dictionary<string, string>
        {
            ["exitCode"] = e.ApplicationExitCode.ToString()
        });
        Services.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Logger.Error("app", "Dispatcher unhandled exception.", e.Exception);
        MessageBox.Show(
            "An unexpected error occurred. Your file queue is still in memory. Original files were not modified. Details are in the local log.",
            "SmartShift",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        Logger.Error("app", "Domain unhandled exception.", e.ExceptionObject as Exception);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Logger.Error("app", "Unobserved task exception.", e.Exception);
        e.SetObserved();
    }
}
