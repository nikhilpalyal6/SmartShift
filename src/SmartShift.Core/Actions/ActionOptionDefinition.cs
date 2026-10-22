namespace SmartShift.Core.Actions;

public sealed record ActionOptionDefinition(
    string Key,
    string Title,
    string Description,
    string ValueKind,
    string? DefaultValue,
    string? ValidationHint);
