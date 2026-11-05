using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace SmartShift.App.Ui;

public sealed class PdfOrganizeItemViewModel : INotifyPropertyChanged
{
    private int _currentPosition;
    private int _rotationDegrees;
    private bool _isSelected;
    private ImageSource? _thumbnail;
    private bool _isThumbnailLoaded;

    public PdfOrganizeItemViewModel(
        int originalPageIndex,
        int inherentRotation,
        double widthPt,
        double heightPt)
    {
        OriginalPageIndex = originalPageIndex;
        InherentRotation = inherentRotation;
        WidthPt = widthPt;
        HeightPt = heightPt;
        _currentPosition = originalPageIndex + 1;
        _rotationDegrees = 0;
    }

    public int OriginalPageIndex { get; }

    public int OriginalPageNumber => OriginalPageIndex + 1;

    public int InherentRotation { get; }

    public double WidthPt { get; }

    public double HeightPt { get; }

    public int CurrentPosition
    {
        get => _currentPosition;
        set
        {
            if (_currentPosition != value)
            {
                _currentPosition = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PositionDisplay));
            }
        }
    }

    public string PositionDisplay => $"#{CurrentPosition}";

    public string OriginalDisplay => $"Orig #{OriginalPageNumber}";

    public int RotationDegrees
    {
        get => _rotationDegrees;
        set
        {
            var normalized = ((value % 360) + 360) % 360;
            if (_rotationDegrees != normalized)
            {
                _rotationDegrees = normalized;
                OnPropertyChanged();
                OnPropertyChanged(nameof(EffectiveRotation));
                OnPropertyChanged(nameof(IsRotated));
                OnPropertyChanged(nameof(RotationDisplay));
            }
        }
    }

    public int EffectiveRotation => ((InherentRotation + _rotationDegrees) % 360 + 360) % 360;

    public bool IsRotated => _rotationDegrees != 0;

    public string RotationDisplay => _rotationDegrees switch
    {
        90 => "↻ 90°",
        180 => "↻ 180°",
        270 => "↺ 90°",
        _ => string.Empty
    };

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

    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            if (!ReferenceEquals(_thumbnail, value))
            {
                _thumbnail = value;
                _isThumbnailLoaded = value != null;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsThumbnailLoaded));
            }
        }
    }

    public bool IsThumbnailLoaded => _isThumbnailLoaded;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
