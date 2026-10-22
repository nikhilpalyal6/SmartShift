using SmartShift.Core.Actions;
using SmartShift.Core.Classification;
using SmartShift.Core.IO;
using SmartShift.Processing.Images;
using SmartShift.Processing.Pdf;
using SmartShift.Processing.Pdf.Preview;

namespace SmartShift.Processing;

public sealed class ProcessorCatalog
{
    private readonly Dictionary<string, IFileProcessor> _processors;

    public ProcessorCatalog(FileClassifier classifier, IOutputPathAllocator allocator)
    {
        _processors = new Dictionary<string, IFileProcessor>
        {
            [ActionIds.ShowImageInfo] = new InspectFileProcessor(ActionIds.ShowImageInfo, classifier),
            [ActionIds.ShowPdfInfo] = new InspectFileProcessor(ActionIds.ShowPdfInfo, classifier),
            [ActionIds.CompressImage] = new ImageFileProcessor(ActionIds.CompressImage, allocator, classifier),
            [ActionIds.ConvertToWebp] = new ImageFileProcessor(ActionIds.ConvertToWebp, allocator, classifier),
            [ActionIds.ConvertToPng] = new ImageFileProcessor(ActionIds.ConvertToPng, allocator, classifier),
            [ActionIds.ConvertToJpeg] = new ImageFileProcessor(ActionIds.ConvertToJpeg, allocator, classifier),
            [ActionIds.ResizeImage] = new ImageFileProcessor(ActionIds.ResizeImage, allocator, classifier),
            [ActionIds.RemoveImageMetadata] = new ImageFileProcessor(ActionIds.RemoveImageMetadata, allocator, classifier),
            [ActionIds.EditImage] = new ImageFileProcessor(ActionIds.EditImage, allocator, classifier),
            [ActionIds.CompressPdf] = new PdfFileProcessor(allocator, classifier),
            [ActionIds.ExportPdfPages] = new PdfExportProcessor(allocator, classifier),
            [ActionIds.ExtractPdfText] = new PdfTextProcessor(allocator, classifier),
            [ActionIds.ConvertPdfToDocx] = new PdfDocxProcessor(allocator, classifier),
            [ActionIds.ConvertPdfToPptx] = new PdfPptxProcessor(allocator, classifier),
            [ActionIds.PreviewPdf] = new PdfPreviewProcessor()
        };
    }

    public IFileProcessor? Find(string actionId)
        => _processors.TryGetValue(actionId, out var processor) ? processor : null;
}
