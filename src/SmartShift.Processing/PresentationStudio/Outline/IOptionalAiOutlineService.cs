using SmartShift.Processing.PresentationStudio.Import;

namespace SmartShift.Processing.PresentationStudio.Outline;

public interface IOptionalAiOutlineService
{
    bool IsAvailable { get; }
    string ProviderName { get; }
    Task<PresentationOutline?> TryGenerateOutlineAsync(
        ExtractedDocumentContent content,
        OutlineGenerationOptions options,
        CancellationToken cancellationToken);
}

public sealed class LocalAiOutlineService : IOptionalAiOutlineService
{
    // SmartShift currently runs completely local-first with deterministic parsing.
    // Cloud AI is prohibited to preserve privacy.
    public bool IsAvailable => false;
    public string ProviderName => "Local AI (Disabled - Deterministic Engine Active)";

    public Task<PresentationOutline?> TryGenerateOutlineAsync(
        ExtractedDocumentContent content,
        OutlineGenerationOptions options,
        CancellationToken cancellationToken)
    {
        // When local models (such as ONNX or local Ollama) are configured by the user,
        // this service will generate AI-assisted summaries. Currently falls back to deterministic.
        return Task.FromResult<PresentationOutline?>(null);
    }
}

