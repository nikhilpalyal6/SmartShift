using System.Windows;
using SmartShift.Core.Drops;

namespace SmartShift.Windows.DragDrop;

/// <summary>
/// Reads file paths from an OLE/WPF data object when CF_HDROP / FileDrop is present.
/// Does not initiate, cancel, or complete a native drag on its own.
/// </summary>
public static class OleFileDropExtractor
{
    public static DropExtractionResult Extract(IDataObject? data, ModifierKeySnapshot? modifiers = null)
    {
        var mods = modifiers ?? NativeInput.CaptureModifiers();
        if (data is null)
        {
            return new DropExtractionResult(FileDropInspection.Invalid("No data object was supplied."), mods);
        }

        string[] formats;
        try
        {
            formats = data.GetFormats() ?? Array.Empty<string>();
        }
        catch (Exception ex)
        {
            return new DropExtractionResult(
                FileDropInspection.Invalid($"Failed to enumerate data formats: {ex.Message}"),
                mods);
        }

        if (!data.GetDataPresent(DataFormats.FileDrop, autoConvert: true))
        {
            return new DropExtractionResult(
                FileDropInspection.Invalid(
                    "IDataObject does not contain CF_HDROP / FileDrop. Low-level mouse hooks cannot supply this payload.",
                    formats),
                mods);
        }

        object? raw;
        try
        {
            raw = data.GetData(DataFormats.FileDrop, autoConvert: true);
        }
        catch (Exception ex)
        {
            return new DropExtractionResult(
                FileDropInspection.Invalid($"FileDrop was advertised but could not be read: {ex.Message}", formats),
                mods);
        }

        if (raw is not string[] paths)
        {
            return new DropExtractionResult(
                FileDropInspection.Invalid(
                    $"FileDrop payload was {raw?.GetType().FullName ?? "null"}, not string[].",
                    formats),
                mods);
        }

        return new DropExtractionResult(FileDropInspector.Inspect(paths, formats), mods);
    }

    /// <summary>
    /// Phase 0 never accepts Move. Explorer may delete the source if a drop target reports Move.
    /// </summary>
    public static DragDropEffects SafeAllowedEffects(bool canInspectFiles)
        => canInspectFiles ? DragDropEffects.Copy : DragDropEffects.None;
}
