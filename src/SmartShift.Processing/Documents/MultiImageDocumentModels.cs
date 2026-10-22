using SmartShift.Core.Classification;

namespace SmartShift.Processing.Documents;

public sealed record MultiImageDocumentRequest(
    IReadOnlyList<ClassifiedFile> Files,
    DocumentOutputMode Mode,
    DocumentPageSettings PageSettings,
    DocumentSlideSettings SlideSettings,
    string OutputDirectory,
    string? CustomFileName = null);

public sealed record MultiImageDocumentResult(
    bool Succeeded,
    string? OutputPath,
    int PageOrSlideCount,
    string StatusMessage,
    IReadOnlyList<string> Errors,
    IReadOnlyDictionary<string, string> Details);
