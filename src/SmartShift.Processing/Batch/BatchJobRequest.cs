using SmartShift.Core.Classification;

namespace SmartShift.Processing.Batch;

public sealed record BatchJobRequest(
    IReadOnlyList<ClassifiedFile> Files,
    string ActionId,
    ProcessingOptions Options,
    string OutputDirectory,
    int MaxConcurrency = 1);
