using SmartShift.Core.Logging;

namespace SmartShift.Infrastructure.Logging;

public sealed class LocalFileLogSink : IDisposable
{
    private readonly DiagnosticLogger _logger;
    private readonly string _path;
    private readonly object _gate = new();

    public LocalFileLogSink(DiagnosticLogger logger, string? directory = null)
    {
        _logger = logger;
        var dir = directory
            ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SmartShift", "logs");
        Directory.CreateDirectory(dir);
        _path = System.IO.Path.Combine(dir, "smartshift.log");
        _logger.EntryAdded += OnEntry;
    }

    public string LogFilePath => _path;

    private void OnEntry(DiagnosticEntry entry)
    {
        try
        {
            lock (_gate)
            {
                File.AppendAllText(_path, entry + Environment.NewLine);
            }
        }
        catch
        {
            // Logging must not interrupt the UI.
        }
    }

    public void Dispose()
    {
        _logger.EntryAdded -= OnEntry;
    }
}
