using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using SmartShift.Core.Classification;
using SmartShift.Core.IO;
using SmartShift.Processing.Batch;

namespace SmartShift.App.Ui;

public sealed class BatchQueueItemViewModel : INotifyPropertyChanged
{
    private BatchItemStatus _status = BatchItemStatus.Pending;
    private string _statusText = "Ready";
    private string? _outputPath;
    private string? _resultMessage;
    private IReadOnlyDictionary<string, string>? _details;
    private bool _isSelected;

    public BatchQueueItemViewModel(ClassifiedFile file)
    {
        File = file;
    }

    public ClassifiedFile File { get; }

    public string FileName => File.FileName;

    public string Extension => string.IsNullOrWhiteSpace(File.Extension) ? "(none)" : File.Extension.TrimStart('.').ToUpperInvariant();

    public string SizeText => FileSizeFormatter.Format(File.LengthBytes);

    public string DimensionsText => File.PixelWidth is int w && File.PixelHeight is int h ? $"{w} × {h} px" : "";

    public Brush FormatTagBrush
    {
        get
        {
            var colorKey = File.Format switch
            {
                DetectedFormat.Png => "AccentBrush",
                DetectedFormat.Jpeg => "WarnBrush",
                DetectedFormat.Webp => "OkBrush",
                _ => "MutedBrush"
            };
            return Application.Current.TryFindResource(colorKey) as Brush ?? Brushes.Gray;
        }
    }

    public BatchItemStatus Status
    {
        get => _status;
        set
        {
            if (_status != value)
            {
                _status = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusBrush));
                OnPropertyChanged(nameof(StatusBackgroundBrush));
                OnPropertyChanged(nameof(CanReveal));
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        set
        {
            if (_statusText != value)
            {
                _statusText = value;
                OnPropertyChanged();
            }
        }
    }

    public Brush StatusBrush
    {
        get
        {
            var key = _status switch
            {
                BatchItemStatus.Succeeded => "OkBrush",
                BatchItemStatus.Failed => "DangerBrush",
                BatchItemStatus.Processing => "AccentBrush",
                BatchItemStatus.Cancelled => "WarnBrush",
                _ => "MutedBrush"
            };
            return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
        }
    }

    public Brush StatusBackgroundBrush
    {
        get
        {
            var key = _status switch
            {
                BatchItemStatus.Succeeded => "OkSoftBrush",
                BatchItemStatus.Failed => "DangerSoftBrush",
                BatchItemStatus.Processing => "AccentSoftBrush",
                BatchItemStatus.Cancelled => "WarnSoftBrush",
                _ => "SurfaceElevatedBrush"
            };
            return Application.Current.TryFindResource(key) as Brush ?? Brushes.Transparent;
        }
    }

    public string? OutputPath
    {
        get => _outputPath;
        set
        {
            if (_outputPath != value)
            {
                _outputPath = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanReveal));
            }
        }
    }

    public bool CanReveal => !string.IsNullOrWhiteSpace(_outputPath) && System.IO.File.Exists(_outputPath);

    public string? ResultMessage
    {
        get => _resultMessage;
        set
        {
            if (_resultMessage != value)
            {
                _resultMessage = value;
                OnPropertyChanged();
            }
        }
    }

    public IReadOnlyDictionary<string, string>? Details
    {
        get => _details;
        set
        {
            if (_details != value)
            {
                _details = value;
                OnPropertyChanged();
            }
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                OnPropertyChanged();
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
