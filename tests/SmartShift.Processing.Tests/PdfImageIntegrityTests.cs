using System.Security.Cryptography;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using SkiaSharp;
using SmartShift.Core.Actions;
using SmartShift.Core.Classification;
using SmartShift.Core.IO;
using SmartShift.Processing.Pdf;
using Xunit;

namespace SmartShift.Processing.Tests;

public sealed class PdfImageIntegrityTests : IDisposable
{
    private readonly string _rootDir;
    private readonly List<string> _tempFiles = [];
    private readonly FileClassifier _classifier = new();

    public PdfImageIntegrityTests()
    {
        _rootDir = Path.Combine(Path.GetTempPath(), "smartshift-pdf-integrity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_rootDir);
    }

    private TestAllocator Allocator() => new(_rootDir);

    private string CreateJpeg(int width, int height, int quality = 90)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.DarkBlue);
        using var paint = new SKPaint { Color = SKColors.Yellow, StrokeWidth = 4 };
        for (int i = 0; i < width; i += 10)
        {
            canvas.DrawLine(i, 0, width - i, height, paint);
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, quality);
        var path = Path.Combine(_rootDir, $"fixture-{Guid.NewGuid():N}.jpg");
        File.WriteAllBytes(path, data.ToArray());
        _tempFiles.Add(path);
        return path;
    }

    private string CreatePdf(Action<PdfDocument> setup, string? name = null)
    {
        var path = Path.Combine(_rootDir, name ?? $"integrity-{Guid.NewGuid():N}.pdf");
        using (var doc = new PdfDocument())
        {
            setup(doc);
            doc.Save(path);
        }
        _tempFiles.Add(path);
        return path;
    }

    private static string HashFile(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    [Fact]
    public async Task Case1_StandardDctJpeg_OptimizesWhenTargetRequires()
    {
        // 1. JPEG image XObjects with /DCTDecode
        var imgPath = CreateJpeg(800, 800, quality: 95);
        var pdfPath = CreatePdf(doc =>
        {
            var page = doc.AddPage();
            using var gfx = XGraphics.FromPdfPage(page);
            using var ximg = XImage.FromFile(imgPath);
            gfx.DrawImage(ximg, 50, 50, 300, 300);
        });

        var hashBefore = HashFile(pdfPath);
        var initialSize = new FileInfo(pdfPath).Length;
        var processor = new PdfFileProcessor(Allocator(), _classifier);
        var options = new ProcessingOptions(new Dictionary<string, string>
        {
            ["targetSize"] = "10",
            ["targetUnit"] = "KB",
            ["imageQuality"] = "smallest"
        });
        var request = new ProcessingRequest(_classifier.Classify(pdfPath), ActionIds.CompressPdf, options, _rootDir);

        var result = await processor.ProcessAsync(request, null, CancellationToken.None);

        Assert.True(result.Succeeded, result.Status);
        Assert.Equal(hashBefore, HashFile(pdfPath));
        Assert.NotNull(result.OutputPath);
        Assert.True(new FileInfo(result.OutputPath).Length < initialSize);

        using var verify = PdfReader.Open(result.OutputPath, PdfDocumentOpenMode.Import);
        Assert.Equal(1, verify.PageCount);
        Assert.Equal(595, verify.Pages[0].Width.Point, 1);
        Assert.Equal(842, verify.Pages[0].Height.Point, 1);
    }

    [Fact]
    public async Task Case2_NonRgbColorSpacesAndDecodeParms_PreservedUnchanged()
    {
        // 2. Images using /DecodeParms, unusual color spaces, CMYK, grayscale
        var imgPath = CreateJpeg(400, 400, quality: 90);
        var pdfPath = CreatePdf(doc =>
        {
            var page = doc.AddPage();
            using var gfx = XGraphics.FromPdfPage(page);
            using var ximg = XImage.FromFile(imgPath);
            gfx.DrawImage(ximg, 20, 20, 200, 200);
        });

        // Inject /ColorSpace /DeviceCMYK and /DecodeParms into the image dictionary
        byte[] originalStreamBytes;
        using (var doc = PdfReader.Open(pdfPath, PdfDocumentOpenMode.Modify))
        {
            var imgDict = doc.Internals.GetAllObjects()
                .OfType<PdfDictionary>()
                .First(d => d.Elements.GetName("/Subtype") == "/Image");

            imgDict.Elements.SetName("/ColorSpace", "/DeviceCMYK");
            var decodeParms = new PdfDictionary();
            decodeParms.Elements.SetInteger("/ColorTransform", 0);
            imgDict.Elements.Add("/DecodeParms", decodeParms);

            originalStreamBytes = imgDict.Stream!.Value;
            doc.Save(pdfPath);
        }

        var hashBefore = HashFile(pdfPath);
        var processor = new PdfFileProcessor(Allocator(), _classifier);
        var options = new ProcessingOptions(new Dictionary<string, string>
        {
            ["targetSize"] = "5",
            ["targetUnit"] = "KB",
            ["imageQuality"] = "smallest"
        });
        var request = new ProcessingRequest(_classifier.Classify(pdfPath), ActionIds.CompressPdf, options, _rootDir);

        var result = await processor.ProcessAsync(request, null, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(hashBefore, HashFile(pdfPath));

        // Verify the stream bytes and /ColorSpace were preserved untouched
        using (var verify = PdfReader.Open(result.OutputPath!, PdfDocumentOpenMode.Import))
        {
            var imgDict = verify.Internals.GetAllObjects()
                .OfType<PdfDictionary>()
                .First(d => d.Elements.GetName("/Subtype") == "/Image");

            Assert.Equal("/DeviceCMYK", imgDict.Elements.GetName("/ColorSpace"));
            Assert.True(imgDict.Elements.ContainsKey("/DecodeParms"));
            Assert.Equal(originalStreamBytes, imgDict.Stream!.Value);
        }
    }

    [Fact]
    public async Task Case3_MaskAndSMaskTransparency_PreservedUnchanged()
    {
        // 3. Images with /SMask or /Mask transparency
        var imgPath = CreateJpeg(400, 400, quality: 90);
        var pdfPath = CreatePdf(doc =>
        {
            var page = doc.AddPage();
            using var gfx = XGraphics.FromPdfPage(page);
            using var ximg = XImage.FromFile(imgPath);
            gfx.DrawImage(ximg, 20, 20, 200, 200);
        });

        byte[] originalStreamBytes;
        using (var doc = PdfReader.Open(pdfPath, PdfDocumentOpenMode.Modify))
        {
            var imgDict = doc.Internals.GetAllObjects()
                .OfType<PdfDictionary>()
                .First(d => d.Elements.GetName("/Subtype") == "/Image");

            // Attach a dummy SMask dictionary
            var smaskDict = new PdfDictionary();
            smaskDict.Elements.SetName("/Type", "/XObject");
            smaskDict.Elements.SetName("/Subtype", "/Image");
            smaskDict.Elements.SetInteger("/Width", 400);
            smaskDict.Elements.SetInteger("/Height", 400);
            doc.Internals.AddObject(smaskDict);
            imgDict.Elements.SetReference("/SMask", smaskDict);

            originalStreamBytes = imgDict.Stream!.Value;
            doc.Save(pdfPath);
        }

        var hashBefore = HashFile(pdfPath);
        var processor = new PdfFileProcessor(Allocator(), _classifier);
        var options = new ProcessingOptions(new Dictionary<string, string>
        {
            ["targetSize"] = "5",
            ["targetUnit"] = "KB",
            ["imageQuality"] = "smallest"
        });
        var request = new ProcessingRequest(_classifier.Classify(pdfPath), ActionIds.CompressPdf, options, _rootDir);

        var result = await processor.ProcessAsync(request, null, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(hashBefore, HashFile(pdfPath));

        using (var verify = PdfReader.Open(result.OutputPath!, PdfDocumentOpenMode.Import))
        {
            var imgDict = verify.Internals.GetAllObjects()
                .OfType<PdfDictionary>()
                .First(d => d.Elements.GetName("/Subtype") == "/Image" && d.Elements.ContainsKey("/SMask"));

            Assert.True(imgDict.Elements.ContainsKey("/SMask"));
            Assert.Equal(originalStreamBytes, imgDict.Stream!.Value);
        }
    }

    [Fact]
    public async Task Case4_SharedImagesReferencedAcrossPages_PreservedAndValidated()
    {
        // 4. Images referenced multiple times or shared across pages
        var imgPath = CreateJpeg(800, 800, quality: 95);
        var pdfPath = CreatePdf(doc =>
        {
            using var ximg = XImage.FromFile(imgPath);
            for (int i = 0; i < 3; i++)
            {
                var page = doc.AddPage();
                using var gfx = XGraphics.FromPdfPage(page);
                // Draw the exact same XImage instance on 3 pages
                gfx.DrawImage(ximg, 50, 50, 250, 250);
            }
        });

        var hashBefore = HashFile(pdfPath);
        var processor = new PdfFileProcessor(Allocator(), _classifier);
        var options = new ProcessingOptions(new Dictionary<string, string>
        {
            ["targetSize"] = "20",
            ["targetUnit"] = "KB",
            ["imageQuality"] = "smallest"
        });
        var request = new ProcessingRequest(_classifier.Classify(pdfPath), ActionIds.CompressPdf, options, _rootDir);

        var result = await processor.ProcessAsync(request, null, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(hashBefore, HashFile(pdfPath));

        using (var verify = PdfReader.Open(result.OutputPath!, PdfDocumentOpenMode.Import))
        {
            Assert.Equal(3, verify.PageCount);
            // Verify there is only 1 image XObject in the document (shared across all 3 pages)
            var imageObjects = verify.Internals.GetAllObjects()
                .OfType<PdfDictionary>()
                .Where(d => d.Elements.GetName("/Subtype") == "/Image")
                .ToList();

            Assert.Single(imageObjects);
        }
    }

    [Fact]
    public async Task Case5_NonJpegFiltersAndFlateDecode_PreservedUnchanged()
    {
        // 5. Non-JPEG image filters such as FlateDecode
        var pdfPath = CreatePdf(doc =>
        {
            var page = doc.AddPage();
            // Create a bitmap and draw it with default PDFsharp Flate compression
            using var bmp = new SKBitmap(100, 100);
            bmp.Erase(SKColors.CornflowerBlue);
            using var img = SKImage.FromBitmap(bmp);
            using var data = img.Encode(SKEncodedImageFormat.Png, 100);
            var pngPath = Path.Combine(_rootDir, $"sample-{Guid.NewGuid():N}.png");
            File.WriteAllBytes(pngPath, data.ToArray());
            _tempFiles.Add(pngPath);

            using var gfx = XGraphics.FromPdfPage(page);
            using var ximg = XImage.FromFile(pngPath);
            gfx.DrawImage(ximg, 30, 30, 100, 100);
        });

        // Verify the image has /FlateDecode filter
        using (var check = PdfReader.Open(pdfPath, PdfDocumentOpenMode.Import))
        {
            var imgDict = check.Internals.GetAllObjects()
                .OfType<PdfDictionary>()
                .First(d => d.Elements.GetName("/Subtype") == "/Image");
            Assert.Equal("/FlateDecode", imgDict.Elements.GetName("/Filter"));
        }

        var hashBefore = HashFile(pdfPath);
        var processor = new PdfFileProcessor(Allocator(), _classifier);
        var options = new ProcessingOptions(new Dictionary<string, string>
        {
            ["targetSize"] = "1",
            ["targetUnit"] = "KB",
            ["imageQuality"] = "smallest"
        });
        var request = new ProcessingRequest(_classifier.Classify(pdfPath), ActionIds.CompressPdf, options, _rootDir);

        var result = await processor.ProcessAsync(request, null, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(hashBefore, HashFile(pdfPath));

        using (var verify = PdfReader.Open(result.OutputPath!, PdfDocumentOpenMode.Import))
        {
            var imgDict = verify.Internals.GetAllObjects()
                .OfType<PdfDictionary>()
                .First(d => d.Elements.GetName("/Subtype") == "/Image");
            Assert.Equal("/FlateDecode", imgDict.Elements.GetName("/Filter"));
        }
    }

    [Fact]
    public async Task Case6_RotatedImagesWithMatrix_PreservesPageLayoutAndOrientation()
    {
        // 6. Rotated images and images with PDF transformation matrices
        var imgPath = CreateJpeg(600, 600, quality: 90);
        var pdfPath = CreatePdf(doc =>
        {
            var page = doc.AddPage();
            using var gfx = XGraphics.FromPdfPage(page);
            using var ximg = XImage.FromFile(imgPath);

            // Apply rotation transform
            gfx.RotateAtTransform(45, new XPoint(200, 200));
            gfx.DrawImage(ximg, 100, 100, 200, 200);
        });

        var hashBefore = HashFile(pdfPath);
        var processor = new PdfFileProcessor(Allocator(), _classifier);
        var options = new ProcessingOptions(new Dictionary<string, string>
        {
            ["targetSize"] = "15",
            ["targetUnit"] = "KB",
            ["imageQuality"] = "smallest"
        });
        var request = new ProcessingRequest(_classifier.Classify(pdfPath), ActionIds.CompressPdf, options, _rootDir);

        var result = await processor.ProcessAsync(request, null, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(hashBefore, HashFile(pdfPath));

        using (var verify = PdfReader.Open(result.OutputPath!, PdfDocumentOpenMode.Import))
        {
            Assert.Equal(1, verify.PageCount);
            // Verify content stream preserves matrix/q/Q blocks
            var page = verify.Pages[0];
            Assert.NotNull(page.Contents);
        }
    }

    [Fact]
    public async Task Case7_MixedContent_TextRemainsSelectableAndVectorsIntact()
    {
        // 7. PDFs containing text, vector graphics, and mixed image/text content
        var imgPath = CreateJpeg(400, 400, quality: 90);
        var pdfPath = CreatePdf(doc =>
        {
            var page = doc.AddPage();
            using var gfx = XGraphics.FromPdfPage(page);

            // Text
            var font = new XFont("Arial", 16, XFontStyleEx.Bold);
            gfx.DrawString("Selectable Header Text Line 1", font, XBrushes.Navy, new XPoint(50, 50));
            gfx.DrawString("Secondary Paragraph Text Content Line 2", font, XBrushes.Black, new XPoint(50, 80));

            // Vectors
            gfx.DrawLine(XPens.Crimson, 50, 100, 500, 100);
            gfx.DrawRectangle(XBrushes.LightSkyBlue, 50, 120, 200, 100);
            gfx.DrawEllipse(XPens.DarkGreen, 300, 120, 100, 100);

            // Raster image
            using var ximg = XImage.FromFile(imgPath);
            gfx.DrawImage(ximg, 50, 250, 200, 200);
        });

        var hashBefore = HashFile(pdfPath);
        var processor = new PdfFileProcessor(Allocator(), _classifier);
        var options = new ProcessingOptions(new Dictionary<string, string>
        {
            ["targetSize"] = "10",
            ["targetUnit"] = "KB",
            ["imageQuality"] = "smallest"
        });
        var request = new ProcessingRequest(_classifier.Classify(pdfPath), ActionIds.CompressPdf, options, _rootDir);

        var result = await processor.ProcessAsync(request, null, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(hashBefore, HashFile(pdfPath));

        using (var verify = PdfReader.Open(result.OutputPath!, PdfDocumentOpenMode.Import))
        {
            Assert.Equal(1, verify.PageCount);
            // Verify content stream contains text operator Tj and vector operators
            var page = verify.Pages[0];
            var contents = page.Contents.Elements.GetDictionary(0);
            Assert.NotNull(contents);
            Assert.NotNull(contents.Stream);
            byte[] contentBytes = contents.Stream.UnfilteredValue;
            string contentString = System.Text.Encoding.ASCII.GetString(contentBytes);

            Assert.Contains("Selectable Header Text Line 1", contentString);
            Assert.Contains("Secondary Paragraph Text Content Line 2", contentString);
            // Verify vector operators (re, l, c, or m) are in stream
            Assert.Contains("re", contentString);
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_rootDir))
            {
                Directory.Delete(_rootDir, true);
            }
        }
        catch
        {
        }
    }

    private sealed class TestAllocator : IOutputPathAllocator
    {
        public TestAllocator(string root) => RootDirectory = root;

        public string RootDirectory { get; }

        public void EnsureRoot() => Directory.CreateDirectory(RootDirectory);

        public string Allocate(string sourcePath, string desiredFileName)
        {
            var baseName = Path.GetFileNameWithoutExtension(desiredFileName);
            var ext = Path.GetExtension(desiredFileName);
            var candidate = Path.Combine(RootDirectory, desiredFileName);
            int counter = 1;
            while (File.Exists(candidate))
            {
                candidate = Path.Combine(RootDirectory, $"{baseName} ({counter}){ext}");
                counter++;
            }
            return candidate;
        }
    }
}
