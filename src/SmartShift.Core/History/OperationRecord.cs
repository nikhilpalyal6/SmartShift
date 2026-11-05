using System.Text.Json.Serialization;

namespace SmartShift.Core.History;

/// <summary>
/// Immutable record of an executed operation across SmartShift.
/// </summary>
public sealed record OperationRecord(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("actionId")] string ActionId,
    [property: JsonPropertyName("actionName")] string ActionName,
    [property: JsonPropertyName("category")] OperationCategory Category,
    [property: JsonPropertyName("inputPaths")] IReadOnlyList<string> InputPaths,
    [property: JsonPropertyName("outputPaths")] IReadOnlyList<string> OutputPaths,
    [property: JsonPropertyName("startedAtUtc")] DateTimeOffset StartedAtUtc,
    [property: JsonPropertyName("completedAtUtc")] DateTimeOffset CompletedAtUtc,
    [property: JsonPropertyName("duration")] TimeSpan Duration,
    [property: JsonPropertyName("status")] OperationStatus Status,
    [property: JsonPropertyName("options")] IReadOnlyDictionary<string, string> Options,
    [property: JsonPropertyName("presetName")] string? PresetName = null,
    [property: JsonPropertyName("failureReason")] string? FailureReason = null,
    [property: JsonPropertyName("retryOfOperationId")] string? RetryOfOperationId = null,
    [property: JsonPropertyName("details")] IReadOnlyDictionary<string, string>? Details = null)
{
    [JsonIgnore]
    public bool IsSuccess => Status == OperationStatus.Completed;

    [JsonIgnore]
    public bool IsFailed => Status == OperationStatus.Failed;

    [JsonIgnore]
    public bool IsCancelled => Status == OperationStatus.Cancelled;

    [JsonIgnore]
    public bool IsRetryAttempt => !string.IsNullOrWhiteSpace(RetryOfOperationId);

    [JsonIgnore]
    public string PrimaryInputFileName =>
        InputPaths.Count > 0 ? Path.GetFileName(InputPaths[0]) : "Unknown Input";

    [JsonIgnore]
    public string? PrimaryOutputPath =>
        OutputPaths.Count > 0 ? OutputPaths[0] : null;

    [JsonIgnore]
    public string PrimaryOutputFileName =>
        PrimaryOutputPath is not null ? Path.GetFileName(PrimaryOutputPath) : "No output";

    [JsonIgnore]
    public bool CanRetry => IsFailed && InputPaths.Count > 0;
}
