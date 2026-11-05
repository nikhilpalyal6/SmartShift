using System.Text.Json;
using SmartShift.Core.Images;

namespace SmartShift.Processing.Images;

/// <summary>
/// File-based persistence store for image optimization presets with built-in fallbacks.
/// </summary>
public sealed class ImagePresetStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _storagePath;
    private readonly object _lock = new();

    public ImagePresetStore(string? storagePath = null)
    {
        _storagePath = storagePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SmartShift",
            "image_presets.json");
    }

    public string StoragePath => _storagePath;

    public IReadOnlyList<ImageOptimizationPreset> GetAll()
    {
        lock (_lock)
        {
            var builtIns = ImageOptimizationPreset.GetBuiltInPresets();
            var userPresets = LoadUserPresets();

            var combined = new List<ImageOptimizationPreset>(builtIns);
            foreach (var user in userPresets)
            {
                if (!combined.Any(p => string.Equals(p.Id, user.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    combined.Add(user);
                }
            }

            return combined;
        }
    }

    public ImageOptimizationPreset? FindById(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        return GetAll().FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    public void SavePreset(ImageOptimizationPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        var errors = preset.Validate();
        if (errors.Count > 0)
        {
            throw new ArgumentException($"Invalid preset: {string.Join(" ", errors)}");
        }

        lock (_lock)
        {
            var userPresets = LoadUserPresets();
            var existingIndex = userPresets.FindIndex(p => string.Equals(p.Id, preset.Id, StringComparison.OrdinalIgnoreCase));

            // User cannot overwrite a built-in with IsBuiltIn=false, but can add/update custom presets
            var presetToSave = preset with { IsBuiltIn = false };

            if (existingIndex >= 0)
            {
                userPresets[existingIndex] = presetToSave;
            }
            else
            {
                userPresets.Add(presetToSave);
            }

            PersistUserPresets(userPresets);
        }
    }

    public bool DeletePreset(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;

        lock (_lock)
        {
            // Built-ins cannot be deleted
            if (ImageOptimizationPreset.GetBuiltInPresets().Any(b => string.Equals(b.Id, id, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            var userPresets = LoadUserPresets();
            var removed = userPresets.RemoveAll(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed)
            {
                PersistUserPresets(userPresets);
            }

            return removed;
        }
    }

    private List<ImageOptimizationPreset> LoadUserPresets()
    {
        if (!File.Exists(_storagePath))
        {
            return [];
        }

        try
        {
            var json = File.ReadAllText(_storagePath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return [];
            }

            var items = JsonSerializer.Deserialize<List<ImageOptimizationPreset>>(json, JsonOptions);
            return items?.Where(p => p.Validate().Count == 0).ToList() ?? [];
        }
        catch
        {
            // Corrupt file recovery: return empty user list so built-ins continue to work
            return [];
        }
    }

    private void PersistUserPresets(List<ImageOptimizationPreset> presets)
    {
        try
        {
            var dir = Path.GetDirectoryName(_storagePath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(presets, JsonOptions);
            var tempPath = _storagePath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _storagePath, overwrite: true);
        }
        catch
        {
            // Best effort persistence
        }
    }
}
