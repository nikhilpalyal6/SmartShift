using SmartShift.Core.Classification;

namespace SmartShift.Core.Actions;

public sealed record ActionRecommendation(
    string ActionId,
    string Title,
    string Description,
    string GoalId,
    string GoalTitle,
    IReadOnlyList<DetectedFormat> ApplicableFormats,
    bool IsAvailable,
    string AvailabilityNote,
    IReadOnlyList<ActionOptionDefinition> Options,
    IReadOnlyList<string> Limitations);
