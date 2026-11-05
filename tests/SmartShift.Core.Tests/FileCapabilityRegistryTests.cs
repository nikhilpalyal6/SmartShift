using SmartShift.Core.Actions;
using SmartShift.Core.Capabilities;
using SmartShift.Core.Classification;
using Xunit;

namespace SmartShift.Core.Tests;

public sealed class FileCapabilityRegistryTests
{
    private readonly FileCapabilityRegistry _registry = new();

    [Fact]
    public void Registry_ContainsAllRequiredFormats()
    {
        var descriptors = _registry.GetAllDescriptors();
        Assert.Contains(descriptors, d => d.Format == DetectedFormat.Png);
        Assert.Contains(descriptors, d => d.Format == DetectedFormat.Jpeg);
        Assert.Contains(descriptors, d => d.Format == DetectedFormat.Webp);
        Assert.Contains(descriptors, d => d.Format == DetectedFormat.Pdf);
        Assert.Contains(descriptors, d => d.Format == DetectedFormat.Docx);
        Assert.Contains(descriptors, d => d.Format == DetectedFormat.Pptx);
        Assert.Contains(descriptors, d => d.Format == DetectedFormat.Xlsx);
        Assert.Contains(descriptors, d => d.Format == DetectedFormat.Txt);
        Assert.Contains(descriptors, d => d.Format == DetectedFormat.Markdown);
    }

    [Theory]
    [InlineData(DetectedFormat.Png, PreviewCapability.Image, true)]
    [InlineData(DetectedFormat.Jpeg, PreviewCapability.Image, true)]
    [InlineData(DetectedFormat.Webp, PreviewCapability.Image, true)]
    [InlineData(DetectedFormat.Pdf, PreviewCapability.Pdf, true)]
    [InlineData(DetectedFormat.Txt, PreviewCapability.Text, true)]
    [InlineData(DetectedFormat.Markdown, PreviewCapability.Text, true)]
    [InlineData(DetectedFormat.Docx, PreviewCapability.Text, true)]
    [InlineData(DetectedFormat.Pptx, PreviewCapability.None, true)]
    [InlineData(DetectedFormat.Xlsx, PreviewCapability.None, false)]
    [InlineData(DetectedFormat.Gif, PreviewCapability.None, false)]
    [InlineData(DetectedFormat.Bmp, PreviewCapability.None, false)]
    [InlineData(DetectedFormat.Tiff, PreviewCapability.None, false)]
    [InlineData(DetectedFormat.Zip, PreviewCapability.None, false)]
    [InlineData(DetectedFormat.Unknown, PreviewCapability.None, false)]
    public void Registry_PreviewAndSupportStatus_MatchRealCapabilities(
        DetectedFormat format,
        PreviewCapability expectedPreview,
        bool expectedSupported)
    {
        var desc = _registry.GetDescriptor(format);
        Assert.Equal(expectedPreview, desc.Preview);
        Assert.Equal(expectedSupported, desc.IsSupported);
        Assert.Equal(expectedPreview, _registry.GetPreviewCapability(format));
        Assert.Equal(expectedSupported, _registry.IsFormatSupported(format));
    }

    [Fact]
    public void Registry_ConversionOutputs_MatchGenuinelyWorkingConverters()
    {
        // PDF outputs match existing converters
        var pdfOutputs = _registry.GetSupportedOutputFormats(DetectedFormat.Pdf);
        Assert.Contains(DetectedFormat.Docx, pdfOutputs);
        Assert.Contains(DetectedFormat.Pptx, pdfOutputs);
        Assert.Contains(DetectedFormat.Txt, pdfOutputs);
        Assert.Contains(DetectedFormat.Png, pdfOutputs);
        Assert.Contains(DetectedFormat.Jpeg, pdfOutputs);

        // Images only convert to working image targets
        var pngOutputs = _registry.GetSupportedOutputFormats(DetectedFormat.Png);
        Assert.Contains(DetectedFormat.Webp, pngOutputs);
        Assert.Contains(DetectedFormat.Jpeg, pngOutputs);
        Assert.DoesNotContain(DetectedFormat.Docx, pngOutputs);

        // DOCX has genuine conversion outputs
        var docxOutputs = _registry.GetSupportedOutputFormats(DetectedFormat.Docx);
        Assert.Contains(DetectedFormat.Pdf, docxOutputs);
        Assert.Contains(DetectedFormat.Pptx, docxOutputs);

        // TXT and Markdown have no conversion outputs
        Assert.Empty(_registry.GetSupportedOutputFormats(DetectedFormat.Txt));
        Assert.Empty(_registry.GetSupportedOutputFormats(DetectedFormat.Markdown));

        // XLSX has NO conversion outputs and NO preview
        var xlsxOutputs = _registry.GetSupportedOutputFormats(DetectedFormat.Xlsx);
        Assert.Empty(xlsxOutputs);
        Assert.Equal(PreviewCapability.None, _registry.GetPreviewCapability(DetectedFormat.Xlsx));

        // Unknown and Zip have NO conversion outputs
        Assert.Empty(_registry.GetSupportedOutputFormats(DetectedFormat.Unknown));
        Assert.Empty(_registry.GetSupportedOutputFormats(DetectedFormat.Zip));
    }

    [Fact]
    public void Registry_AvailableActions_ReflectGenuinelyAvailableServices()
    {
        // Images have editing and transforms
        var pngActions = _registry.GetAvailableActionIds(DetectedFormat.Png);
        Assert.Contains(ActionIds.ShowImageInfo, pngActions);
        Assert.Contains(ActionIds.CompressImage, pngActions);
        Assert.Contains(ActionIds.EditImage, pngActions);
        Assert.Contains(ActionIds.ResizeImage, pngActions);

        // PDF has preview and conversions
        var pdfActions = _registry.GetAvailableActionIds(DetectedFormat.Pdf);
        Assert.Contains(ActionIds.ShowPdfInfo, pdfActions);
        Assert.Contains(ActionIds.CompressPdf, pdfActions);
        Assert.Contains(ActionIds.ConvertPdfToDocx, pdfActions);
        Assert.Contains(ActionIds.ConvertPdfToPptx, pdfActions);
        Assert.Contains(ActionIds.PreviewPdf, pdfActions);
        Assert.Contains(ActionIds.SplitPdf, pdfActions);

        // DOCX has info and presentation import, plus conversions and compression
        var docxActions = _registry.GetAvailableActionIds(DetectedFormat.Docx);
        Assert.Contains(ActionIds.ShowDocumentInfo, docxActions);
        Assert.Contains(ActionIds.ImportToPresentationStudio, docxActions);
        Assert.Contains(ActionIds.ConvertDocxToPdf, docxActions);
        Assert.Contains(ActionIds.ConvertDocxToPptx, docxActions);
        Assert.Contains(ActionIds.CompressDocx, docxActions);
        Assert.DoesNotContain(ActionIds.CompressPdf, docxActions);

        // XLSX only has inspection, no false conversion/edit actions
        var xlsxActions = _registry.GetAvailableActionIds(DetectedFormat.Xlsx);
        Assert.Contains(ActionIds.ShowDocumentInfo, xlsxActions);
        Assert.DoesNotContain(ActionIds.EditImage, xlsxActions);
        Assert.DoesNotContain(ActionIds.CompressPdf, xlsxActions);
        Assert.DoesNotContain(ActionIds.ConvertPdfToDocx, xlsxActions);

        // ZIP and Unknown have NO actions
        Assert.Empty(_registry.GetAvailableActionIds(DetectedFormat.Zip));
        Assert.Empty(_registry.GetAvailableActionIds(DetectedFormat.Unknown));
    }

    [Fact]
    public void Registry_FindDescriptorByExtension_IsCaseInsensitive()
    {
        var docx1 = _registry.FindDescriptorByExtension(".docx");
        var docx2 = _registry.FindDescriptorByExtension("DOCX");
        Assert.NotNull(docx1);
        Assert.Same(docx1, docx2);
        Assert.Equal(DetectedFormat.Docx, docx1.Format);

        var pdf = _registry.FindDescriptorByExtension(".PDF");
        Assert.NotNull(pdf);
        Assert.Equal(DetectedFormat.Pdf, pdf.Format);

        var unknown = _registry.FindDescriptorByExtension(".xyz123");
        Assert.Null(unknown);
    }

    [Fact]
    public void Registry_GetFileFilter_GeneratesCompleteFilterString()
    {
        var filter = _registry.GetFileFilter(includeAllFiles: true);

        Assert.Contains("*.png", filter);
        Assert.Contains("*.jpg", filter);
        Assert.Contains("*.webp", filter);
        Assert.Contains("*.pdf", filter);
        Assert.Contains("*.docx", filter);
        Assert.Contains("*.pptx", filter);
        Assert.Contains("*.txt", filter);
        Assert.Contains("*.md", filter);
        Assert.Contains("All Files (*.*)|*.*", filter);
    }
}
