using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using SmartShift.Core.Actions;

namespace SmartShift.App.Ui;

public sealed class RadialActionItem : INotifyPropertyChanged
{
    private bool _isSelected;
    private double _x;
    private double _y;

    public RadialActionItem(ActionRecommendation recommendation, int index, int totalCount)
    {
        Recommendation = recommendation;
        ActionId = recommendation.ActionId;
        FullTitle = recommendation.Title;
        Description = recommendation.Description;
        GoalTitle = recommendation.GoalTitle;
        IsAvailable = recommendation.IsAvailable;
        Badge = recommendation.IsAvailable ? "Available" : "Not implemented";
        Index = index;
        HotkeyText = (index + 1).ToString();

        // Distribute evenly around circle, starting from top (-90 degrees)
        AngleDegrees = RadialLayoutCalculator.CalculateAngle(index, Math.Max(1, totalCount));

        Title = ActionId switch
        {
            ActionIds.CompressImage => "Compress",
            ActionIds.ConvertToWebp => "WebP",
            ActionIds.ResizeImage => "Resize",
            ActionIds.RemoveImageMetadata => "Strip EXIF",
            ActionIds.EditImage => "Edit",
            ActionIds.ShowImageInfo or ActionIds.ShowPdfInfo => "Details",
            ActionIds.CompressPdf => "Compress",
            ActionIds.ExportPdfPages => "Export",
            ActionIds.ExtractPdfText => "Extract",
            ActionIds.ConvertPdfToDocx => "Word",
            ActionIds.ConvertPdfToPptx => "PowerPoint",
            ActionIds.PreviewPdf => "Preview",
            _ => recommendation.Title
        };

        IconKey = ActionId switch
        {
            ActionIds.CompressImage or ActionIds.CompressPdf => "IconCompressGeometry",
            ActionIds.ConvertToWebp => "IconConvertGeometry",
            ActionIds.ResizeImage => "IconResizeGeometry",
            ActionIds.RemoveImageMetadata => "IconShieldGeometry",
            ActionIds.EditImage => "IconEditGeometry",
            ActionIds.ExportPdfPages => "IconExportGeometry",
            ActionIds.ExtractPdfText or ActionIds.ConvertPdfToDocx => "IconDocumentGeometry",
            ActionIds.ConvertPdfToPptx => "IconPresentationGeometry",
            ActionIds.PreviewPdf => "IconEyeGeometry",
            _ => "IconInfoGeometry"
        };

        var app = System.Windows.Application.Current;
        BadgeBrush = (Brush)app.FindResource(recommendation.IsAvailable ? "OkSoftBrush" : "WarnSoftBrush");
        BadgeForeground = (Brush)app.FindResource(recommendation.IsAvailable ? "OkBrush" : "WarnBrush");
    }

    public ActionRecommendation Recommendation { get; }
    public string ActionId { get; }
    public string Title { get; }
    public string FullTitle { get; }
    public string Description { get; }
    public string GoalTitle { get; }
    public bool IsAvailable { get; }
    public string Badge { get; }
    public string IconKey { get; }
    public int Index { get; }
    public string HotkeyText { get; }
    public double AngleDegrees { get; }
    public Brush BadgeBrush { get; }
    public Brush BadgeForeground { get; }

    public Geometry IconGeometry
    {
        get
        {
            try
            {
                if (System.Windows.Application.Current.TryFindResource(IconKey) is Geometry geo)
                {
                    return geo;
                }
            }
            catch
            {
            }

            return Geometry.Empty;
        }
    }

    private Geometry? _petalGeometry;
    private double _centerX;
    private double _centerY;

    public Geometry? PetalGeometry
    {
        get => _petalGeometry;
        set
        {
            if (_petalGeometry != value)
            {
                _petalGeometry = value;
                OnPropertyChanged();
            }
        }
    }

    public double CenterX
    {
        get => _centerX;
        set
        {
            if (Math.Abs(_centerX - value) > 0.1)
            {
                _centerX = value;
                OnPropertyChanged();
            }
        }
    }

    public double CenterY
    {
        get => _centerY;
        set
        {
            if (Math.Abs(_centerY - value) > 0.1)
            {
                _centerY = value;
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

    public double X
    {
        get => _x;
        set
        {
            if (Math.Abs(_x - value) > 0.1)
            {
                _x = value;
                OnPropertyChanged();
            }
        }
    }

    public double Y
    {
        get => _y;
        set
        {
            if (Math.Abs(_y - value) > 0.1)
            {
                _y = value;
                OnPropertyChanged();
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
