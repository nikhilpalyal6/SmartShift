using SmartShift.Core.Classification;

namespace SmartShift.Processing.Batch;

public sealed record BatchItemResult(
    ClassifiedFile Input,
    BatchItemStatus Status,
    string? OutputPath,
    string StatusMessage,
    IReadOnlyList<string> Errors,
    IReadOnlyDictionary<string, string> Details,
    TimeSpan Duration);
