namespace SmartShift.Processing;

public sealed class ProcessingOptions
{
    private readonly Dictionary<string, string> _values;

    public ProcessingOptions(IReadOnlyDictionary<string, string>? values = null)
    {
        _values = values is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyDictionary<string, string> Values => _values;

    public string? this[string key] => _values.TryGetValue(key, out var value) ? value : null;
}
