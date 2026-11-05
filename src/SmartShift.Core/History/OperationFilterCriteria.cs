namespace SmartShift.Core.History;

/// <summary>
/// Criteria for searching and filtering processing history records.
/// </summary>
public sealed record OperationFilterCriteria(
    string? SearchText = null,
    OperationStatus? Status = null,
    OperationCategory Category = OperationCategory.All,
    DateTimeOffset? FromDateUtc = null,
    DateTimeOffset? ToDateUtc = null)
{
    public bool Matches(OperationRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        // Status filter
        if (Status.HasValue && record.Status != Status.Value)
        {
            return false;
        }

        // Category filter
        if (Category != OperationCategory.All && record.Category != Category)
        {
            return false;
        }

        // Date range filter
        if (FromDateUtc.HasValue && record.StartedAtUtc < FromDateUtc.Value)
        {
            return false;
        }

        if (ToDateUtc.HasValue && record.StartedAtUtc > ToDateUtc.Value)
        {
            return false;
        }

        // Search text
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var query = SearchText.Trim();
            var matchesAction = record.ActionName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                record.ActionId.Contains(query, StringComparison.OrdinalIgnoreCase);

            var matchesInput = record.InputPaths.Any(p => Path.GetFileName(p).Contains(query, StringComparison.OrdinalIgnoreCase));
            var matchesOutput = record.OutputPaths.Any(p => Path.GetFileName(p).Contains(query, StringComparison.OrdinalIgnoreCase));
            var matchesError = record.FailureReason?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false;
            var matchesPreset = record.PresetName?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false;

            if (!matchesAction && !matchesInput && !matchesOutput && !matchesError && !matchesPreset)
            {
                return false;
            }
        }

        return true;
    }
}
