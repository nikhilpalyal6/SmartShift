using SmartShift.Core.Classification;

namespace SmartShift.Processing;

public sealed record ProcessingRequest(
    ClassifiedFile Input,
    string ActionId,
    ProcessingOptions Options,
    string OutputDirectory);
