using System.Diagnostics;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using DocumentFormat.OpenXml.Validation;
using SkiaSharp;
using SmartShift.Processing.PresentationStudio.Models;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace SmartShift.Processing.PresentationStudio.Export;

public sealed class PresentationPptxExporter : IPresentationPptxExporter
{
    public async Task<string> ExportAsync(
        PresentationDeck presentation,
        string outputPath,
        IProgress<ProcessingProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(presentation);
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            throw new ArgumentException("Output path must be provided.", nameof(outputPath));
        }

        cancellationToken.ThrowIfCancellationRequested();

        return await Task.Run(() => ExportInternal(presentation, outputPath, progress, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
    }

    private static string ExportInternal(
        PresentationDeck presentation,
        string outputPath,
        IProgress<ProcessingProgress>? progress,
        CancellationToken cancellationToken)
    {
        var validationErrors = presentation.Validate();
        if (validationErrors.Count > 0)
        {
            throw new InvalidOperationException($"Cannot export presentation: {string.Join("; ", validationErrors)}");
        }

        var tempPartial = outputPath + ".partial";
        if (File.Exists(tempPartial))
        {
            try { File.Delete(tempPartial); } catch { }
        }

        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        try
        {
            progress?.Report(new ProcessingProgress(5, "Initializing PowerPoint package…"));

            using (var presentationDoc = PresentationDocument.Create(tempPartial, PresentationDocumentType.Presentation))
            {
                var presPart = presentationDoc.AddPresentationPart();
                presPart.Presentation = new Presentation();

                long slideWidthEmu = presentation.Dimensions.WidthEmu;
                long slideHeightEmu = presentation.Dimensions.HeightEmu;

                presPart.Presentation.SlideSize = new SlideSize
                {
                    Cx = (int)slideWidthEmu,
                    Cy = (int)slideHeightEmu,
                    Type = presentation.Dimensions.AspectRatio == SlideAspectRatio.Standard4x3
                        ? SlideSizeValues.Screen4x3
                        : SlideSizeValues.Screen16x9
                };

                presPart.Presentation.NotesSize = new NotesSize
                {
                    Cx = (int)slideHeightEmu,
                    Cy = (int)slideWidthEmu
                };

                // Setup Master and Layout parts
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

                var theme = presentation.Theme;
                var themeBgHex = CleanHex(theme.BackgroundHex);
                var themeTextHex = CleanHex(theme.PrimaryTextHex);
                var themeAccentHex = CleanHex(theme.AccentHex);
                var themeMutedHex = CleanHex(theme.MutedTextHex);

                for (int i = 0; i < presentation.Slides.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var slide = presentation.Slides[i];

                    int pct = 10 + (int)(80.0 * (i + 1) / presentation.Slides.Count);
                    progress?.Report(new ProcessingProgress(pct, $"Building slide {i + 1} of {presentation.Slides.Count}…"));

                    var slidePart = presPart.AddNewPart<SlidePart>();
                    var shapeTree = CreateShapeTree();
                    slidePart.Slide = new Slide(new CommonSlideData(shapeTree));
                    slidePart.AddPart(layoutPart);

                    // Slide Background
                    slidePart.Slide.CommonSlideData!.Background = new Background(
                        new BackgroundProperties(
                            new A.SolidFill(new A.RgbColorModelHex { Val = themeBgHex })));

                    uint shapeCounter = 2U;

                    // Build shapes according to slide layout
                    BuildSlideShapes(
                        slide,
                        shapeTree,
                        ref shapeCounter,
                        slidePart,
                        slideWidthEmu,
                        slideHeightEmu,
                        theme,
                        cancellationToken);

                    slideIdList.AppendChild(new SlideId
                    {
                        Id = (uint)(256 + i),
                        RelationshipId = presPart.GetIdOfPart(slidePart)
                    });
                }

                presPart.Presentation.Save();
            }

            progress?.Report(new ProcessingProgress(92, "Validating OpenXML presentation package…"));
            cancellationToken.ThrowIfCancellationRequested();

            using (var verifyPackage = PresentationDocument.Open(tempPartial, false))
            {
                var validator = new OpenXmlValidator();
                var errors = validator.Validate(verifyPackage).ToList();

                var actualSlides = verifyPackage.PresentationPart?.SlideParts.Count() ?? 0;
                if (actualSlides != presentation.Slides.Count)
                {
                    throw new InvalidOperationException($"PPTX validation error: Expected {presentation.Slides.Count} slides, but created {actualSlides}.");
                }
            }

            // Atomic file move
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
            File.Move(tempPartial, outputPath);

            progress?.Report(new ProcessingProgress(100, "Presentation exported successfully."));
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

    private static void BuildSlideShapes(
        PresentationSlide slide,
        ShapeTree shapeTree,
        ref uint shapeCounter,
        SlidePart slidePart,
        long slideW,
        long slideH,
        PresentationTheme theme,
        CancellationToken cancellationToken)
    {
        switch (slide.Layout)
        {
            case PresentationLayoutType.Title:
                BuildTitleLayout(slide, shapeTree, ref shapeCounter, slideW, slideH, theme);
                break;

            case PresentationLayoutType.SectionDivider:
                BuildSectionDividerLayout(slide, shapeTree, ref shapeCounter, slideW, slideH, theme);
                break;

            case PresentationLayoutType.TwoColumn:
                BuildTwoColumnLayout(slide, shapeTree, ref shapeCounter, slideW, slideH, theme);
                break;

            case PresentationLayoutType.TextWithImage:
                BuildTextWithImageLayout(slide, shapeTree, ref shapeCounter, slidePart, slideW, slideH, theme, cancellationToken);
                break;

            case PresentationLayoutType.ImageFocused:
                BuildImageFocusedLayout(slide, shapeTree, ref shapeCounter, slidePart, slideW, slideH, theme, cancellationToken);
                break;

            case PresentationLayoutType.Blank:
                // No text or picture shapes, blank canvas
                break;

            case PresentationLayoutType.TitleAndContent:
            default:
                BuildTitleAndContentLayout(slide, shapeTree, ref shapeCounter, slideW, slideH, theme);
                break;
        }

        // Add subtle source provenance text in bottom-right footer if available
        if (!string.IsNullOrWhiteSpace(slide.SourceProvenance))
        {
            long footX = (long)(slideW * 0.50);
            long footY = (long)(slideH * 0.93);
            long footW = (long)(slideW * 0.44);
            long footH = (long)(slideH * 0.05);

            AddSingleRunTextBox(
                shapeTree,
                shapeCounter++,
                footX,
                footY,
                footW,
                footH,
                slide.SourceProvenance,
                theme.FontFamily,
                10.0,
                theme.MutedTextHex,
                isBold: false,
                isCenter: false);
        }
    }

    private static void BuildTitleLayout(
        PresentationSlide slide,
        ShapeTree shapeTree,
        ref uint shapeCounter,
        long slideW,
        long slideH,
        PresentationTheme theme)
    {
        long titleX = (long)(slideW * 0.10);
        long titleY = (long)(slideH * 0.32);
        long titleW = (long)(slideW * 0.80);
        long titleH = (long)(slideH * 0.25);

        AddSingleRunTextBox(
            shapeTree,
            shapeCounter++,
            titleX,
            titleY,
            titleW,
            titleH,
            slide.Title,
            theme.FontFamily,
            theme.TitleFontSize + 8,
            theme.AccentHex,
            isBold: true,
            isCenter: theme.Alignment == SlideTextAlignment.Center);

        var subtitle = slide.PrimaryParagraph;
        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            long subX = (long)(slideW * 0.10);
            long subY = (long)(slideH * 0.58);
            long subW = (long)(slideW * 0.80);
            long subH = (long)(slideH * 0.20);

            AddSingleRunTextBox(
                shapeTree,
                shapeCounter++,
                subX,
                subY,
                subW,
                subH,
                subtitle,
                theme.FontFamily,
                theme.BodyFontSize + 2,
                theme.PrimaryTextHex,
                isBold: false,
                isCenter: theme.Alignment == SlideTextAlignment.Center);
        }
    }

    private static void BuildSectionDividerLayout(
        PresentationSlide slide,
        ShapeTree shapeTree,
        ref uint shapeCounter,
        long slideW,
        long slideH,
        PresentationTheme theme)
    {
        long titleX = (long)(slideW * 0.10);
        long titleY = (long)(slideH * 0.40);
        long titleW = (long)(slideW * 0.80);
        long titleH = (long)(slideH * 0.25);

        AddSingleRunTextBox(
            shapeTree,
            shapeCounter++,
            titleX,
            titleY,
            titleW,
            titleH,
            slide.Title,
            theme.FontFamily,
            theme.TitleFontSize + 4,
            theme.AccentHex,
            isBold: true,
            isCenter: true);

        var para = slide.PrimaryParagraph;
        if (!string.IsNullOrWhiteSpace(para))
        {
            long paraX = (long)(slideW * 0.15);
            long paraY = (long)(slideH * 0.65);
            long paraW = (long)(slideW * 0.70);
            long paraH = (long)(slideH * 0.15);

            AddSingleRunTextBox(
                shapeTree,
                shapeCounter++,
                paraX,
                paraY,
                paraW,
                paraH,
                para,
                theme.FontFamily,
                theme.BodyFontSize,
                theme.MutedTextHex,
                isBold: false,
                isCenter: true);
        }
    }

    private static void BuildTitleAndContentLayout(
        PresentationSlide slide,
        ShapeTree shapeTree,
        ref uint shapeCounter,
        long slideW,
        long slideH,
        PresentationTheme theme)
    {
        // Slide Title
        long titleX = (long)(slideW * 0.06);
        long titleY = (long)(slideH * 0.07);
        long titleW = (long)(slideW * 0.88);
        long titleH = (long)(slideH * 0.14);

        AddSingleRunTextBox(
            shapeTree,
            shapeCounter++,
            titleX,
            titleY,
            titleW,
            titleH,
            slide.Title,
            theme.FontFamily,
            theme.TitleFontSize,
            theme.PrimaryTextHex,
            isBold: true,
            isCenter: theme.Alignment == SlideTextAlignment.Center);

        // Content area: Bullets or Paragraph
        long bodyX = (long)(slideW * 0.06);
        long bodyY = (long)(slideH * 0.24);
        long bodyW = (long)(slideW * 0.88);
        long bodyH = (long)(slideH * 0.66);

        AddContentBlockShape(
            shapeTree,
            shapeCounter++,
            bodyX,
            bodyY,
            bodyW,
            bodyH,
            slide.TextBlocks.Where(b => b.ColumnIndex == 0).ToList(),
            theme);
    }

    private static void BuildTwoColumnLayout(
        PresentationSlide slide,
        ShapeTree shapeTree,
        ref uint shapeCounter,
        long slideW,
        long slideH,
        PresentationTheme theme)
    {
        // Slide Title
        long titleX = (long)(slideW * 0.06);
        long titleY = (long)(slideH * 0.07);
        long titleW = (long)(slideW * 0.88);
        long titleH = (long)(slideH * 0.14);

        AddSingleRunTextBox(
            shapeTree,
            shapeCounter++,
            titleX,
            titleY,
            titleW,
            titleH,
            slide.Title,
            theme.FontFamily,
            theme.TitleFontSize,
            theme.PrimaryTextHex,
            isBold: true,
            isCenter: theme.Alignment == SlideTextAlignment.Center);

        // Column 1 (Left)
        long col1X = (long)(slideW * 0.06);
        long col1Y = (long)(slideH * 0.24);
        long col1W = (long)(slideW * 0.42);
        long colH = (long)(slideH * 0.66);

        AddContentBlockShape(
            shapeTree,
            shapeCounter++,
            col1X,
            col1Y,
            col1W,
            colH,
            slide.TextBlocks.Where(b => b.ColumnIndex == 0).ToList(),
            theme);

        // Column 2 (Right)
        long col2X = (long)(slideW * 0.52);
        long col2Y = (long)(slideH * 0.24);
        long col2W = (long)(slideW * 0.42);

        AddContentBlockShape(
            shapeTree,
            shapeCounter++,
            col2X,
            col2Y,
            col2W,
            colH,
            slide.TextBlocks.Where(b => b.ColumnIndex == 1).ToList(),
            theme);
    }

    private static void BuildTextWithImageLayout(
        PresentationSlide slide,
        ShapeTree shapeTree,
        ref uint shapeCounter,
        SlidePart slidePart,
        long slideW,
        long slideH,
        PresentationTheme theme,
        CancellationToken cancellationToken)
    {
        // Slide Title
        long titleX = (long)(slideW * 0.06);
        long titleY = (long)(slideH * 0.07);
        long titleW = (long)(slideW * 0.88);
        long titleH = (long)(slideH * 0.14);

        AddSingleRunTextBox(
            shapeTree,
            shapeCounter++,
            titleX,
            titleY,
            titleW,
            titleH,
            slide.Title,
            theme.FontFamily,
            theme.TitleFontSize,
            theme.PrimaryTextHex,
            isBold: true,
            isCenter: false);

        // Left text block (45% width)
        long textX = (long)(slideW * 0.06);
        long textY = (long)(slideH * 0.24);
        long textW = (long)(slideW * 0.44);
        long bodyH = (long)(slideH * 0.66);

        AddContentBlockShape(
            shapeTree,
            shapeCounter++,
            textX,
            textY,
            textW,
            bodyH,
            slide.TextBlocks.Where(b => b.ColumnIndex == 0).ToList(),
            theme);

        // Right image container (42% width)
        if (slide.Image is not null && File.Exists(slide.Image.ImagePath))
        {
            long boxX = (long)(slideW * 0.54);
            long boxY = (long)(slideH * 0.24);
            long boxW = (long)(slideW * 0.40);
            long boxH = bodyH;

            AddImageShape(
                shapeTree,
                shapeCounter++,
                slidePart,
                slide.Image.ImagePath,
                boxX,
                boxY,
                boxW,
                boxH,
                cancellationToken);
        }
    }

    private static void BuildImageFocusedLayout(
        PresentationSlide slide,
        ShapeTree shapeTree,
        ref uint shapeCounter,
        SlidePart slidePart,
        long slideW,
        long slideH,
        PresentationTheme theme,
        CancellationToken cancellationToken)
    {
        // Slide Title
        long titleX = (long)(slideW * 0.06);
        long titleY = (long)(slideH * 0.05);
        long titleW = (long)(slideW * 0.88);
        long titleH = (long)(slideH * 0.12);

        AddSingleRunTextBox(
            shapeTree,
            shapeCounter++,
            titleX,
            titleY,
            titleW,
            titleH,
            slide.Title,
            theme.FontFamily,
            theme.TitleFontSize,
            theme.PrimaryTextHex,
            isBold: true,
            isCenter: true);

        // Center Image
        if (slide.Image is not null && File.Exists(slide.Image.ImagePath))
        {
            long imgBoxX = (long)(slideW * 0.15);
            long imgBoxY = (long)(slideH * 0.20);
            long imgBoxW = (long)(slideW * 0.70);
            long imgBoxH = (long)(slideH * 0.72);

            AddImageShape(
                shapeTree,
                shapeCounter++,
                slidePart,
                slide.Image.ImagePath,
                imgBoxX,
                imgBoxY,
                imgBoxW,
                imgBoxH,
                cancellationToken);
        }
    }

    private static void AddSingleRunTextBox(
        ShapeTree shapeTree,
        uint shapeId,
        long x,
        long y,
        long w,
        long h,
        string text,
        string fontFamily,
        double fontSizePt,
        string colorHex,
        bool isBold,
        bool isCenter)
    {
        var runProps = new A.RunProperties
        {
            FontSize = (int)Math.Round(fontSizePt * 100),
            Bold = isBold
        };
        // ECMA-376 schema order: solidFill before latinFont
        runProps.AppendChild(new A.SolidFill(new A.RgbColorModelHex { Val = CleanHex(colorHex) }));
        runProps.AppendChild(new A.LatinFont { Typeface = fontFamily });

        var shape = new Shape(
            new NonVisualShapeProperties(
                new NonVisualDrawingProperties { Id = shapeId, Name = $"Text {shapeId}" },
                new NonVisualShapeDrawingProperties(new A.ShapeLocks { NoGrouping = true }),
                new ApplicationNonVisualDrawingProperties()),
            new ShapeProperties(
                new A.Transform2D(
                    new A.Offset { X = x, Y = y },
                    new A.Extents { Cx = w, Cy = h }),
                new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle },
                new A.NoFill()),
            new TextBody(
                new A.BodyProperties { Wrap = A.TextWrappingValues.Square, RightToLeftColumns = false },
                new A.ListStyle(),
                new A.Paragraph(
                    new A.ParagraphProperties
                    {
                        Alignment = isCenter ? A.TextAlignmentTypeValues.Center : A.TextAlignmentTypeValues.Left
                    },
                    new A.Run(runProps, new A.Text(text ?? string.Empty))
                )
            ));

        shapeTree.AppendChild(shape);
    }

    private static void AddContentBlockShape(
        ShapeTree shapeTree,
        uint shapeId,
        long x,
        long y,
        long w,
        long h,
        IReadOnlyList<SlideTextBlock> blocks,
        PresentationTheme theme)
    {
        var textBody = new TextBody(
            new A.BodyProperties { Wrap = A.TextWrappingValues.Square, RightToLeftColumns = false },
            new A.ListStyle());

        var accentClean = CleanHex(theme.AccentHex);
        var textClean = CleanHex(theme.PrimaryTextHex);
        var font = theme.FontFamily;
        int fontSizeHundredths = (int)Math.Round(theme.BodyFontSize * 100);

        bool hasAnyContent = false;

        foreach (var block in blocks)
        {
            if (block.Kind == SlideTextBlockKind.BulletList)
            {
                foreach (var line in block.Lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    hasAnyContent = true;

                    // ECMA-376 schema order for pPr: buClr, then buFont, then buChar
                    var pPr = new A.ParagraphProperties
                    {
                        Alignment = A.TextAlignmentTypeValues.Left,
                        LeftMargin = 304800, // 24pt indent
                        Indent = -228600    // -18pt bullet offset
                    };
                    pPr.AppendChild(new A.BulletColor(new A.RgbColorModelHex { Val = accentClean }));
                    pPr.AppendChild(new A.BulletFont { Typeface = "Arial" });
                    pPr.AppendChild(new A.CharacterBullet { Char = "•" });

                    var runProps = new A.RunProperties { FontSize = fontSizeHundredths };
                    runProps.AppendChild(new A.SolidFill(new A.RgbColorModelHex { Val = textClean }));
                    runProps.AppendChild(new A.LatinFont { Typeface = font });

                    var p = new A.Paragraph(pPr, new A.Run(runProps, new A.Text(line)));
                    textBody.AppendChild(p);
                }
            }
            else
            {
                // Paragraph text
                foreach (var line in block.Lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    hasAnyContent = true;

                    var pPr = new A.ParagraphProperties
                    {
                        Alignment = A.TextAlignmentTypeValues.Left
                    };

                    var runProps = new A.RunProperties { FontSize = fontSizeHundredths };
                    runProps.AppendChild(new A.SolidFill(new A.RgbColorModelHex { Val = textClean }));
                    runProps.AppendChild(new A.LatinFont { Typeface = font });

                    var p = new A.Paragraph(pPr, new A.Run(runProps, new A.Text(line)));
                    textBody.AppendChild(p);
                }
            }
        }

        if (!hasAnyContent)
        {
            // Empty placeholder paragraph
            textBody.AppendChild(new A.Paragraph(new A.EndParagraphRunProperties()));
        }

        var shape = new Shape(
            new NonVisualShapeProperties(
                new NonVisualDrawingProperties { Id = shapeId, Name = $"Body {shapeId}" },
                new NonVisualShapeDrawingProperties(new A.ShapeLocks { NoGrouping = true }),
                new ApplicationNonVisualDrawingProperties()),
            new ShapeProperties(
                new A.Transform2D(
                    new A.Offset { X = x, Y = y },
                    new A.Extents { Cx = w, Cy = h }),
                new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle },
                new A.NoFill()),
            textBody);

        shapeTree.AppendChild(shape);
    }

    private static void AddImageShape(
        ShapeTree shapeTree,
        uint shapeId,
        SlidePart slidePart,
        string imagePath,
        long boxX,
        long boxY,
        long boxW,
        long boxH,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Calculate aspect-ratio fitted bounds
        int imgW = 800;
        int imgH = 600;
        try
        {
            using var codec = SKCodec.Create(imagePath);
            if (codec is not null)
            {
                imgW = codec.Info.Width;
                imgH = codec.Info.Height;
            }
        }
        catch { }

        double scaleX = (double)boxW / imgW;
        double scaleY = (double)boxH / imgH;
        double scale = Math.Min(scaleX, scaleY);

        long finalW = (long)Math.Round(imgW * scale);
        long finalH = (long)Math.Round(imgH * scale);
        long finalX = boxX + (boxW - finalW) / 2;
        long finalY = boxY + (boxH - finalH) / 2;

        var ext = Path.GetExtension(imagePath).ToLowerInvariant();
        var partType = ext switch
        {
            ".png" => ImagePartType.Png,
            ".jpg" or ".jpeg" => ImagePartType.Jpeg,
            _ => ImagePartType.Png
        };

        var imagePart = slidePart.AddImagePart(partType);
        using (var fs = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            imagePart.FeedData(fs);
        }
        string relId = slidePart.GetIdOfPart(imagePart);

        var picture = new Picture(
            new NonVisualPictureProperties(
                new NonVisualDrawingProperties { Id = shapeId, Name = $"Picture {shapeId}" },
                new NonVisualPictureDrawingProperties(new A.PictureLocks { NoChangeAspect = true }),
                new ApplicationNonVisualDrawingProperties()),
            new BlipFill(
                new A.Blip { Embed = relId },
                new A.Stretch(new A.FillRectangle())),
            new ShapeProperties(
                new A.Transform2D(
                    new A.Offset { X = finalX, Y = finalY },
                    new A.Extents { Cx = finalW, Cy = finalH }),
                new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle }));

        shapeTree.AppendChild(picture);
    }

    private static ShapeTree CreateShapeTree()
    {
        return new ShapeTree(
            new NonVisualGroupShapeProperties(
                new NonVisualDrawingProperties { Id = 1U, Name = "" },
                new NonVisualGroupShapeDrawingProperties(),
                new ApplicationNonVisualDrawingProperties()),
            new GroupShapeProperties(new A.TransformGroup()));
    }

    private static string CleanHex(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return "FFFFFF";
        return hex.TrimStart('#').ToUpperInvariant();
    }
}

