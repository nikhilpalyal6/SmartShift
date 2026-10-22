using System.IO;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using DocumentFormat.OpenXml.Validation;
using SkiaSharp;
using SmartShift.Core.Classification;
using SmartShift.Core.IO;
using SmartShift.Processing;
using SmartShift.Processing.Images;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace SmartShift.Processing.Documents;

public static class MultiImagePptxGenerator
{
    public static string GeneratePptx(
        IReadOnlyList<ClassifiedFile> files,
        DocumentSlideSettings settings,
        string outputPath,
        IProgress<ProcessingProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (files is null || files.Count == 0)
        {
            throw new ArgumentException("At least one image is required to generate a PowerPoint presentation.", nameof(files));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var tempPartial = outputPath + ".partial";
        if (File.Exists(tempPartial))
        {
            try { File.Delete(tempPartial); } catch { }
        }

        try
        {
            progress?.Report(new ProcessingProgress(5, "Initializing PowerPoint presentation…"));

            using (var presentationDoc = PresentationDocument.Create(tempPartial, PresentationDocumentType.Presentation))
            {
                var presPart = presentationDoc.AddPresentationPart();
                presPart.Presentation = new Presentation();

                // Determine default slide dimensions based on settings and first image (if ImageMatch)
                int refW = 1920;
                int refH = 1080;
                if (files.Count > 0 && File.Exists(files[0].Path))
                {
                    try
                    {
                        using var firstDecoded = SkiaImagePipeline.Decode(files[0].Path, cancellationToken);
                        refW = firstDecoded.Bitmap.Width;
                        refH = firstDecoded.Bitmap.Height;
                    }
                    catch { }
                }

                var defaultLayout = DocumentLayoutEngine.ComputePptxSlideLayout(refW, refH, settings);
                long slideWidthEmu = (long)defaultLayout.TargetWidth;
                long slideHeightEmu = (long)defaultLayout.TargetHeight;

                presPart.Presentation.SlideSize = new SlideSize
                {
                    Cx = (int)slideWidthEmu,
                    Cy = (int)slideHeightEmu,
                    Type = settings.SlideSize switch
                    {
                        SlideSizeOption.Standard4x3 => SlideSizeValues.Screen4x3,
                        _ => SlideSizeValues.Screen16x9
                    }
                };

                presPart.Presentation.NotesSize = new NotesSize
                {
                    Cx = (int)slideHeightEmu,
                    Cy = (int)slideWidthEmu
                };

                // Setup master and layout parts
                var masterPart = presPart.AddNewPart<SlideMasterPart>();
                masterPart.SlideMaster = new SlideMaster(
                    new CommonSlideData(CreateShapeTree()),
                    new ColorMap
                    {
                        Background1 = A.ColorSchemeIndexValues.Light1,
                        Text1 = A.ColorSchemeIndexValues.Dark1,
                        Background2 = A.ColorSchemeIndexValues.Light2,
                        Text2 = A.ColorSchemeIndexValues.Dark2,
                        Accent1 = A.ColorSchemeIndexValues.Accent1,
                        Accent2 = A.ColorSchemeIndexValues.Accent2,
                        Accent3 = A.ColorSchemeIndexValues.Accent3,
                        Accent4 = A.ColorSchemeIndexValues.Accent4,
                        Accent5 = A.ColorSchemeIndexValues.Accent5,
                        Accent6 = A.ColorSchemeIndexValues.Accent6,
                        Hyperlink = A.ColorSchemeIndexValues.Hyperlink,
                        FollowedHyperlink = A.ColorSchemeIndexValues.FollowedHyperlink
                    });

                var layoutPart = masterPart.AddNewPart<SlideLayoutPart>();
                layoutPart.SlideLayout = new SlideLayout(new CommonSlideData(CreateShapeTree()))
                {
                    Type = SlideLayoutValues.Blank
                };

                masterPart.SlideMaster.SlideLayoutIdList = new SlideLayoutIdList(
                    new SlideLayoutId { Id = 2147483649U, RelationshipId = masterPart.GetIdOfPart(layoutPart) });

                presPart.Presentation.SlideMasterIdList = new SlideMasterIdList(
                    new SlideMasterId { Id = 2147483648U, RelationshipId = presPart.GetIdOfPart(masterPart) });

                var slideIdList = new SlideIdList();
                presPart.Presentation.SlideIdList = slideIdList;

                // Add one slide per image
                for (int i = 0; i < files.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var file = files[i];

                    int pct = 10 + (int)(80.0 * (i + 1) / files.Count);
                    progress?.Report(new ProcessingProgress(pct, $"Adding slide {i + 1} of {files.Count} ({file.FileName})…"));

                    if (!File.Exists(file.Path))
                    {
                        throw new FileNotFoundException($"Source image was not found: {file.Path}", file.Path);
                    }

                    using var decoded = SkiaImagePipeline.Decode(file.Path, cancellationToken);
                    var workingBitmap = decoded.Bitmap;

                    var layout = DocumentLayoutEngine.ComputePptxSlideLayout(workingBitmap.Width, workingBitmap.Height, settings);

                    var slidePart = presPart.AddNewPart<SlidePart>();
                    var shapeTree = CreateShapeTree();
                    slidePart.Slide = new Slide(new CommonSlideData(shapeTree));
                    slidePart.AddPart(layoutPart);

                    // Optional background color
                    if (!string.IsNullOrWhiteSpace(settings.BackgroundHex) && !settings.BackgroundHex.Equals("#FFFFFF", StringComparison.OrdinalIgnoreCase))
                    {
                        var hexColor = settings.BackgroundHex.TrimStart('#');
                        slidePart.Slide.CommonSlideData!.Background = new Background(
                            new BackgroundProperties(
                                new A.SolidFill(new A.RgbColorModelHex { Val = hexColor })));
                    }

                    // Prepare cropped bitmap if FillCrop requested
                    SKBitmap? cropped = null;
                    try
                    {
                        SKBitmap bitmapToEmbed = workingBitmap;
                        if (layout.SourceCropRect.HasValue)
                        {
                            var rect = layout.SourceCropRect.Value;
                            cropped = new SKBitmap(rect.Width, rect.Height, workingBitmap.ColorType, workingBitmap.AlphaType);
                            if (workingBitmap.ExtractSubset(cropped, rect))
                            {
                                bitmapToEmbed = cropped;
                            }
                            else
                            {
                                cropped.Dispose();
                                cropped = null;
                            }
                        }

                        using var ms = new MemoryStream();
                        bitmapToEmbed.Encode(ms, SKEncodedImageFormat.Png, 95);
                        ms.Position = 0;

                        var imagePart = slidePart.AddImagePart(ImagePartType.Png);
                        imagePart.FeedData(ms);
                        string relId = slidePart.GetIdOfPart(imagePart);

                        // Insert editable picture shape
                        AddPictureShape(
                            shapeTree,
                            relId,
                            $"Picture {i + 1}",
                            (long)layout.ImageX,
                            (long)layout.ImageY,
                            (long)layout.ImageWidth,
                            (long)layout.ImageHeight);
                    }
                    finally
                    {
                        cropped?.Dispose();
                    }

                    slideIdList.AppendChild(new SlideId
                    {
                        Id = (uint)(256 + i),
                        RelationshipId = presPart.GetIdOfPart(slidePart)
                    });
                }

                presPart.Presentation.Save();
            }

            progress?.Report(new ProcessingProgress(92, "Validating PowerPoint package structure…"));
            cancellationToken.ThrowIfCancellationRequested();

            // Validate OpenXML structure and slide count
            using (var verifyPackage = PresentationDocument.Open(tempPartial, false))
            {
                var validator = new OpenXmlValidator();
                var errors = validator.Validate(verifyPackage).ToList();

                var actualSlides = verifyPackage.PresentationPart?.SlideParts.Count() ?? 0;
                if (actualSlides != files.Count)
                {
                    throw new InvalidOperationException($"PPTX validation failed: Expected {files.Count} slides, but created {actualSlides}.");
                }
            }

            // Atomic promotion
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
            File.Move(tempPartial, outputPath);

            progress?.Report(new ProcessingProgress(100, "PowerPoint generation complete."));
            return outputPath;
        }
        catch
        {
            if (File.Exists(tempPartial))
            {
                try { File.Delete(tempPartial); } catch { }
            }
            throw;
        }
    }

    private static ShapeTree CreateShapeTree()
    {
        return new ShapeTree(
            new P.NonVisualGroupShapeProperties(
                new P.NonVisualDrawingProperties { Id = 1U, Name = "" },
                new P.NonVisualGroupShapeDrawingProperties(),
                new ApplicationNonVisualDrawingProperties()),
            new GroupShapeProperties(new A.TransformGroup()));
    }

    private static void AddPictureShape(ShapeTree shapeTree, string relId, string name, long x, long y, long cx, long cy)
    {
        uint shapeId = (uint)Random.Shared.Next(10, 100000);
        var picture = new P.Picture(
            new P.NonVisualPictureProperties(
                new P.NonVisualDrawingProperties { Id = shapeId, Name = name },
                new P.NonVisualPictureDrawingProperties(new A.PictureLocks { NoChangeAspect = true }),
                new ApplicationNonVisualDrawingProperties()),
            new P.BlipFill(
                new A.Blip { Embed = relId },
                new A.Stretch(new A.FillRectangle())),
            new P.ShapeProperties(
                new A.Transform2D(
                    new A.Offset { X = x, Y = y },
                    new A.Extents { Cx = cx, Cy = cy }),
                new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle }));

        shapeTree.AppendChild(picture);
    }
}
