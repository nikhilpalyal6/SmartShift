using System.ComponentModel;
using System.Runtime.CompilerServices;
using SmartShift.Processing.PresentationStudio.Outline;

namespace SmartShift.App.Ui;

public sealed class OutlineItemViewModel : INotifyPropertyChanged
{
    private int _sequenceNumber;
    private bool _canMoveUp;
    private bool _canMoveDown;

    public OutlineItemViewModel(OutlineSlideDraft draft, int sequenceNumber)
    {
        Draft = draft;
        _sequenceNumber = sequenceNumber;
    }

    public OutlineSlideDraft Draft { get; }

    public int SequenceNumber
    {
        get => _sequenceNumber;
        set
        {
            if (_sequenceNumber != value)
            {
                _sequenceNumber = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SequenceText));
            }
        }
    }

    public string SequenceText => $"Slide {_sequenceNumber}";

    public string Title
    {
        get => Draft.Title;
        set
        {
            if (Draft.Title != value)
            {
                Draft.Title = value;
                OnPropertyChanged();
            }
        }
    }

    public string SummaryText
    {
        get
        {
            if (Draft.BulletPoints.Count > 0)
            {
                return $"{Draft.BulletPoints.Count} item{(Draft.BulletPoints.Count == 1 ? "" : "s")}: " + string.Join(" · ", Draft.BulletPoints.Take(3));
            }
            if (!string.IsNullOrWhiteSpace(Draft.ParagraphText))
            {
                return Draft.ParagraphText.Length > 80 ? Draft.ParagraphText[..80] + "…" : Draft.ParagraphText;
            }
            return Draft.SuggestedLayout.ToString();
        }
    }

    public string? ProvenanceText => Draft.SourceProvenance;

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
