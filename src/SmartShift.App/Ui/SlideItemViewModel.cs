using System.ComponentModel;
using System.Runtime.CompilerServices;
using SmartShift.Processing.PresentationStudio.Models;

namespace SmartShift.App.Ui;

public sealed class SlideItemViewModel : INotifyPropertyChanged
{
    private int _slideNumber;
    private bool _canMoveUp;
    private bool _canMoveDown;
    private bool _isSelected;

    public SlideItemViewModel(PresentationSlide slide, int slideNumber)
    {
        Slide = slide;
        _slideNumber = slideNumber;
    }

    public PresentationSlide Slide { get; }

    public int SlideNumber
    {
        get => _slideNumber;
        set
        {
            if (_slideNumber != value)
            {
                _slideNumber = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SlideNumberText));
            }
        }
    }

    public string SlideNumberText => $"{_slideNumber}";

    public string Title
    {
        get => string.IsNullOrWhiteSpace(Slide.Title) ? "(Untitled Slide)" : Slide.Title;
        set
        {
            if (Slide.Title != value)
            {
                Slide.Title = value;
                OnPropertyChanged();
            }
        }
    }

    public string LayoutBadge => Slide.Layout switch
    {
        PresentationLayoutType.Title => "TITLE",
        PresentationLayoutType.SectionDivider => "DIVIDER",
        PresentationLayoutType.TwoColumn => "2-COL",
        PresentationLayoutType.TextWithImage => "TEXT+IMG",
        PresentationLayoutType.ImageFocused => "IMAGE",
        PresentationLayoutType.Blank => "BLANK",
        _ => "CONTENT"
    };

    public string SummaryPreview
    {
        get
        {
            if (Slide.Layout == PresentationLayoutType.Blank) return "Blank canvas";
            if (Slide.Layout == PresentationLayoutType.Title) return "Title slide";
            if (Slide.Layout == PresentationLayoutType.SectionDivider) return "Section header";

            int bullets = Slide.PrimaryBullets.Count;
            if (Slide.Layout == PresentationLayoutType.TwoColumn)
            {
                bullets += Slide.SecondaryBullets.Count;
                return $"{bullets} items across 2 cols";
            }
            if (Slide.Layout == PresentationLayoutType.TextWithImage)
            {
                return $"{bullets} bullet{(bullets == 1 ? "" : "s")} + Image";
            }
            if (Slide.Layout == PresentationLayoutType.ImageFocused)
            {
                return Slide.Image is not null ? "Image focus" : "Image placeholder";
            }
            return $"{bullets} bullet point{(bullets == 1 ? "" : "s")}";
        }
    }

    public bool HasOverflow => Slide.HasLikelyOverflow(out _);

    public string OverflowWarning
    {
        get
        {
            Slide.HasLikelyOverflow(out var warning);
            return warning;
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

    public void NotifySlideChanged()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(LayoutBadge));
        OnPropertyChanged(nameof(SummaryPreview));
        OnPropertyChanged(nameof(HasOverflow));
        OnPropertyChanged(nameof(OverflowWarning));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
