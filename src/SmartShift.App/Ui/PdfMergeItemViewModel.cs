using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using SmartShift.Core.IO;
using SmartShift.Processing.Pdf;

namespace SmartShift.App.Ui;

public sealed class PdfMergeItemViewModel : INotifyPropertyChanged
{
    private int _positionIndex;
    private bool _canMoveUp;
    private bool _canMoveDown;
    private PdfMergeItemValidation _validation;

    public PdfMergeItemViewModel(PdfMergeItemValidation validation, int positionIndex)
    {
        _validation = validation;
        _positionIndex = positionIndex;
    }

    public string FilePath => _validation.FilePath;
    public string FileName => _validation.FileName;
    public long FileSizeBytes => _validation.FileSizeBytes;
    public string SizeText => FileSizeFormatter.Format(_validation.FileSizeBytes);
    public int PageCount => _validation.PageCount;
    public string PageCountText => _validation.IsValid ? $"{_validation.PageCount} page{(_validation.PageCount == 1 ? "" : "s")}" : "—";

    public bool IsValid => _validation.IsValid;
    public bool IsEncrypted => _validation.IsEncrypted;
    public bool IsDuplicate => _validation.IsDuplicate;
    public string? ErrorReason => _validation.ErrorReason;

    public void UpdateValidation(PdfMergeItemValidation validation)
    {
        _validation = validation;
        OnPropertyChanged(nameof(IsValid));
        OnPropertyChanged(nameof(IsEncrypted));
        OnPropertyChanged(nameof(IsDuplicate));
        OnPropertyChanged(nameof(ErrorReason));
        OnPropertyChanged(nameof(PageCount));
        OnPropertyChanged(nameof(PageCountText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusBrush));
        OnPropertyChanged(nameof(StatusBackgroundBrush));
    }

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

    public string StatusText
    {
        get
        {
            if (IsDuplicate) return "Duplicate";
            if (IsEncrypted) return "Encrypted";
            if (!IsValid) return "Invalid";
            return "Valid";
        }
    }

    public Visibility ErrorVisibility => IsValid ? Visibility.Collapsed : Visibility.Visible;
    public Visibility DuplicateVisibility => IsDuplicate ? Visibility.Visible : Visibility.Collapsed;

    public Brush StatusBrush
    {
        get
        {
            if (IsDuplicate) return Application.Current.TryFindResource("WarnBrush") as Brush ?? Brushes.Orange;
            if (!IsValid) return Application.Current.TryFindResource("DangerBrush") as Brush ?? Brushes.Red;
            return Application.Current.TryFindResource("OkBrush") as Brush ?? Brushes.LightGreen;
        }
    }

    public Brush StatusBackgroundBrush
    {
        get
        {
            if (IsDuplicate) return Application.Current.TryFindResource("WarnSoftBrush") as Brush ?? Brushes.DarkOrange;
            if (!IsValid) return Application.Current.TryFindResource("DangerSoftBrush") as Brush ?? Brushes.DarkRed;
            return Application.Current.TryFindResource("OkSoftBrush") as Brush ?? Brushes.DarkGreen;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
