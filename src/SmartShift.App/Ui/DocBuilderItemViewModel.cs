using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using SmartShift.Core.Classification;
using SmartShift.Core.IO;

namespace SmartShift.App.Ui;

public sealed class DocBuilderItemViewModel : INotifyPropertyChanged
{
    private int _positionIndex;
    private bool _canMoveUp;
    private bool _canMoveDown;

    public DocBuilderItemViewModel(ClassifiedFile file, int positionIndex)
    {
        File = file;
        _positionIndex = positionIndex;
    }

    public ClassifiedFile File { get; }

    public int PositionIndex
    {
        get => _positionIndex;
        set
        {
            if (_positionIndex != value)
            {
                _positionIndex = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PositionText));
            }
        }
    }

    public string PositionText => $"#{_positionIndex}";

    public string FileName => File.FileName;

    public string Extension => string.IsNullOrWhiteSpace(File.Extension) ? "" : File.Extension.TrimStart('.').ToUpperInvariant();

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

    public bool CanMoveUp
    {
        get => _canMoveUp;
        set
        {
            if (_canMoveUp != value)
            {
                _canMoveUp = value;
                OnPropertyChanged();
            }
        }
    }

    public bool CanMoveDown
    {
        get => _canMoveDown;
        set
        {
            if (_canMoveDown != value)
            {
                _canMoveDown = value;
                OnPropertyChanged();
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
