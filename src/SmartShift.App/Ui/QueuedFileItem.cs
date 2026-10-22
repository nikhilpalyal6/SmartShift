using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SmartShift.Core.Classification;
using SmartShift.Core.IO;
using SmartShift.Processing.Pdf.Preview;

namespace SmartShift.App.Ui;

public sealed class QueuedFileItem : INotifyPropertyChanged
{
    private ImageSource? _thumbnail;
    private bool _isSelected;

    public QueuedFileItem(ClassifiedFile file)
    {
        File = file;
        LoadThumbnailSafe();
    }

    public ClassifiedFile File { get; }

    public string FileName => File.FileName;

    public string Extension => string.IsNullOrWhiteSpace(File.Extension) ? "(none)" : File.Extension.ToUpperInvariant();

    public string SizeText => FileSizeFormatter.Format(File.LengthBytes);

    public string TypeText => File.DisplayType;

    public string StatusText => File.Status;

    public string SupportText => File.IsSupportedCategory ? "Supported" : "Unsupported";

    public bool HasThumbnail => _thumbnail is not null;

    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        private set
        {
            _thumbnail = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasThumbnail));
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

    public Brush SupportBrush
    {
        get
        {
            var key = File.IsSupportedCategory ? "OkSoftBrush" : "WarnSoftBrush";
            return (Brush)System.Windows.Application.Current.FindResource(key);
        }
    }

    public Brush SupportForeground
    {
        get
        {
            var key = File.IsSupportedCategory ? "OkBrush" : "WarnBrush";
            return (Brush)System.Windows.Application.Current.FindResource(key);
        }
    }

    public Brush FormatTagBrush
    {
        get
        {
            var colorKey = File.Format switch
            {
                DetectedFormat.Png => "AccentBrush",
                DetectedFormat.Jpeg => "WarnBrush",
                DetectedFormat.Webp => "OkBrush",
                DetectedFormat.Pdf => "DangerBrush",
                _ => File.Extension.ToLowerInvariant() switch
                {
                    ".docx" or ".doc" => "AccentBrush",
                    ".pptx" or ".ppt" => "WarnBrush",
                    ".xlsx" or ".xls" or ".csv" => "OkBrush",
                    _ => "MutedBrush"
                }
            };
            return (Brush)System.Windows.Application.Current.FindResource(colorKey);
        }
    }

    public string CategoryName => File.Format switch
    {
        DetectedFormat.Png or DetectedFormat.Jpeg or DetectedFormat.Webp or DetectedFormat.Gif or DetectedFormat.Bmp or DetectedFormat.Tiff => "Image",
        DetectedFormat.Pdf => "PDF Document",
        _ => File.Extension.ToLowerInvariant() switch
        {
            ".docx" or ".doc" => "Word Document",
            ".pptx" or ".ppt" => "PowerPoint",
            ".xlsx" or ".xls" or ".csv" => "Spreadsheet",
            ".mp4" or ".mov" or ".avi" or ".mkv" or ".webm" or ".wmv" => "Video",
            ".txt" or ".md" or ".log" or ".json" or ".xml" => "Text Document",
            _ => "File"
        }
    };

    public string CategoryIconKey => File.Format switch
    {
        DetectedFormat.Png or DetectedFormat.Jpeg or DetectedFormat.Webp or DetectedFormat.Gif or DetectedFormat.Bmp or DetectedFormat.Tiff => "IconImageGeometry",
        DetectedFormat.Pdf => "IconPdfGeometry",
        _ => File.Extension.ToLowerInvariant() switch
        {
            ".docx" or ".doc" => "IconWordGeometry",
            ".pptx" or ".ppt" => "IconPptxGeometry",
            ".xlsx" or ".xls" or ".csv" => "IconExcelGeometry",
            ".mp4" or ".mov" or ".avi" or ".mkv" or ".webm" or ".wmv" => "IconVideoGeometry",
            ".txt" or ".md" or ".log" or ".json" or ".xml" => "IconTextGeometry",
            _ => "IconFileUnknownGeometry"
        }
    };

    public string DimensionsText
    {
        get
        {
            if (File.PixelWidth is int w && File.PixelHeight is int h)
            {
                return $"{w} × {h} px";
            }

            return File.PdfVersion ?? "";
        }
    }

    private void LoadThumbnailSafe()
    {
        if (!File.Exists || File.LengthBytes > 50 * 1024 * 1024)
        {
            return;
        }

        if (File.Format is DetectedFormat.Png or DetectedFormat.Jpeg or DetectedFormat.Webp)
        {
            try
            {
                using var stream = new FileStream(File.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                ms.Position = 0;

                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 96;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();
                _thumbnail = bmp;
            }
            catch
            {
                // Thumbnail is best-effort; vector fallback is displayed
            }
        }
        else if (File.Format == DetectedFormat.Pdf)
        {
            _ = LoadPdfThumbnailAsync();
        }
    }

    private async Task LoadPdfThumbnailAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var pageData = await Task.Run(() =>
                PdfPreviewRenderer.RenderPageAsync(File.Path, 0, 72.0, cts.Token), cts.Token).ConfigureAwait(false);

            if (pageData is not null)
            {
                var bmp = BitmapSource.Create(
                    pageData.PixelWidth,
                    pageData.PixelHeight,
                    96, 96,
                    PixelFormats.Bgra32,
                    null,
                    pageData.BgraBytes,
                    pageData.Stride);
                bmp.Freeze();

                _thumbnail = bmp;
                OnPropertyChanged(nameof(Thumbnail));
                OnPropertyChanged(nameof(HasThumbnail));
            }
        }
        catch
        {
            // PDF thumbnail is best effort; vector icon is displayed cleanly
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}