using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using SmartShift.Core.History;
using SmartShift.Core.IO;
using SmartShift.Windows.Shell;

namespace SmartShift.App.Ui;

public sealed class HistoryEntryViewModel : INotifyPropertyChanged
{
    private readonly OperationRecord _record;

    public HistoryEntryViewModel(OperationRecord record)
    {
        _record = record ?? throw new ArgumentNullException(nameof(record));
    }

    public OperationRecord Record => _record;

    public string Id => _record.Id;

    public string ActionName => _record.ActionName;

    public string ActionId => _record.ActionId;

    public OperationCategory Category => _record.Category;

    public OperationStatus Status => _record.Status;

    public string PrimaryInputFileName => _record.PrimaryInputFileName;

    public string PrimaryOutputFileName => _record.PrimaryOutputFileName;

    public string? PrimaryOutputPath => _record.PrimaryOutputPath;

    public bool HasOutput => !string.IsNullOrWhiteSpace(PrimaryOutputPath);

    public bool IsOutputAvailable => HasOutput && File.Exists(PrimaryOutputPath);

    public bool IsOutputMissing => HasOutput && !File.Exists(PrimaryOutputPath);

    public string OutputAvailabilityText =>
        !HasOutput ? "No output produced" : (IsOutputAvailable ? "Available on disk" : "Missing or moved");

    public Brush OutputAvailabilityBrush
    {
        get
        {
            if (!HasOutput) return Application.Current.TryFindResource("MutedBrush") as Brush ?? Brushes.Gray;
            return IsOutputAvailable
                ? Application.Current.TryFindResource("OkBrush") as Brush ?? Brushes.Green
                : Application.Current.TryFindResource("WarnBrush") as Brush ?? Brushes.Orange;
        }
    }

    public string FormattedTimestamp =>
        _record.StartedAtUtc.ToLocalTime().ToString("MMM dd, yyyy  HH:mm:ss");

    public string FormattedDuration =>
        _record.Duration.TotalSeconds >= 1.0
            ? $"{_record.Duration.TotalSeconds:F1}s"
            : $"{_record.Duration.TotalMilliseconds:F0}ms";

    public string StatusBadgeText => _record.Status switch
    {
        OperationStatus.Completed => "✓ Success",
        OperationStatus.Failed => "✕ Failed",
        OperationStatus.Cancelled => "⊘ Cancelled",
        _ => _record.Status.ToString()
    };

    public Brush StatusBrush
    {
        get
        {
            var key = _record.Status switch
            {
                OperationStatus.Completed => "OkBrush",
                OperationStatus.Failed => "DangerBrush",
                OperationStatus.Cancelled => "WarnBrush",
                _ => "MutedBrush"
            };
            return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
        }
    }

    public Brush StatusBackgroundBrush
    {
        get
        {
            var key = _record.Status switch
            {
                OperationStatus.Completed => "OkSoftBrush",
                OperationStatus.Failed => "DangerSoftBrush",
                OperationStatus.Cancelled => "WarnSoftBrush",
                _ => "SurfaceElevatedBrush"
            };
            return Application.Current.TryFindResource(key) as Brush ?? Brushes.Transparent;
        }
    }

    public bool CanRetry => _record.CanRetry;

    public bool IsRetryAttempt => _record.IsRetryAttempt;

    public string? FailureReason => _record.FailureReason;

    public string FormattedOutputSize
    {
        get
        {
            if (IsOutputAvailable)
            {
                try
                {
                    var len = new FileInfo(PrimaryOutputPath!).Length;
                    return FileSizeFormatter.Format(len);
                }
                catch
                {
                    return "Inaccessible";
                }
            }
            return "-";
        }
    }

    public void OpenOutput()
    {
        if (IsOutputAvailable)
        {
            ShellFolders.OpenFile(PrimaryOutputPath!);
        }
    }

    public void RevealInFolder()
    {
        if (IsOutputAvailable)
        {
            ShellFolders.RevealInExplorer(PrimaryOutputPath!);
        }
    }

    public void CopyOutputPath()
    {
        if (HasOutput)
        {
            Clipboard.SetText(PrimaryOutputPath!);
        }
    }

    public void RefreshState()
    {
        OnPropertyChanged(nameof(IsOutputAvailable));
        OnPropertyChanged(nameof(IsOutputMissing));
        OnPropertyChanged(nameof(OutputAvailabilityText));
        OnPropertyChanged(nameof(OutputAvailabilityBrush));
        OnPropertyChanged(nameof(FormattedOutputSize));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
