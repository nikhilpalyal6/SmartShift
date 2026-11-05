using SmartShift.Processing.PresentationStudio.Import;

namespace SmartShift.Processing.PresentationStudio.Outline;

public interface IOutlineGenerator
{
    PresentationOutline GenerateOutline(ExtractedDocumentContent content, OutlineGenerationOptions options);
}

