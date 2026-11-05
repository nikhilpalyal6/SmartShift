using System.Text.Json.Serialization;
using SmartShift.Core.Actions;
using SmartShift.Core.History;

namespace SmartShift.Core.Workflows;

/// <summary>
/// Reusable action configuration workflow.
/// </summary>
public sealed record SavedWorkflow(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("category")] OperationCategory Category,
    [property: JsonPropertyName("actionId")] string ActionId,
    [property: JsonPropertyName("options")] IReadOnlyDictionary<string, string> Options,
    [property: JsonPropertyName("createdAtUtc")] DateTimeOffset CreatedAtUtc,
    [property: JsonPropertyName("updatedAtUtc")] DateTimeOffset UpdatedAtUtc,
    [property: JsonPropertyName("isBuiltIn")] bool IsBuiltIn = false)
{
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(Id))
        {
            errors.Add("Workflow ID cannot be empty.");
        }

        if (string.IsNullOrWhiteSpace(Name))
        {
            errors.Add("Workflow name cannot be empty.");
        }

        if (string.IsNullOrWhiteSpace(ActionId))
        {
            errors.Add("Action ID cannot be empty.");
        }
        else
        {
            var optionErrors = ActionOptionValidator.Validate(ActionId, Options);
            errors.AddRange(optionErrors);
        }

        return errors;
    }

    public static IReadOnlyList<SavedWorkflow> GetBuiltInWorkflows() =>
    [
        new(
            Id: "wf-webp-web",
            Name: "Fast Web Delivery (WebP 80)",
            Description: "Converts images to WebP format at 80% quality with metadata stripped.",
            Category: OperationCategory.Image,
            ActionId: ActionIds.ConvertToWebp,
            Options: new Dictionary<string, string> { ["quality"] = "80", ["stripMetadata"] = "true" },
            CreatedAtUtc: DateTimeOffset.UtcNow,
            UpdatedAtUtc: DateTimeOffset.UtcNow,
            IsBuiltIn: true),

        new(
            Id: "wf-png-archive",
            Name: "Archive Quality (PNG Lossless)",
            Description: "Converts or compresses images into lossless PNG preserving full transparency.",
            Category: OperationCategory.Image,
            ActionId: ActionIds.ConvertToPng,
            Options: new Dictionary<string, string> { ["quality"] = "95" },
            CreatedAtUtc: DateTimeOffset.UtcNow,
            UpdatedAtUtc: DateTimeOffset.UtcNow,
            IsBuiltIn: true),

        new(
            Id: "wf-email-jpeg",
            Name: "Compact Email (JPEG 60)",
            Description: "Compresses images for email attachments with metadata stripped.",
            Category: OperationCategory.Image,
            ActionId: ActionIds.ConvertToJpeg,
            Options: new Dictionary<string, string> { ["quality"] = "60", ["stripMetadata"] = "true" },
            CreatedAtUtc: DateTimeOffset.UtcNow,
            UpdatedAtUtc: DateTimeOffset.UtcNow,
            IsBuiltIn: true),

        new(
            Id: "wf-pdf-compress",
            Name: "Standard PDF Compression",
            Description: "Compresses PDF documents using balanced image recompression.",
            Category: OperationCategory.Pdf,
            ActionId: ActionIds.CompressPdf,
            Options: new Dictionary<string, string> { ["imageQuality"] = "balanced" },
            CreatedAtUtc: DateTimeOffset.UtcNow,
            UpdatedAtUtc: DateTimeOffset.UtcNow,
            IsBuiltIn: true),

        new(
            Id: "wf-pdf-docx",
            Name: "PDF to Word Document (DOCX)",
            Description: "Converts PDF document pages to editable DOCX format.",
            Category: OperationCategory.Pdf,
            ActionId: ActionIds.ConvertPdfToDocx,
            Options: new Dictionary<string, string>(),
            CreatedAtUtc: DateTimeOffset.UtcNow,
            UpdatedAtUtc: DateTimeOffset.UtcNow,
            IsBuiltIn: true)
    ];
}
