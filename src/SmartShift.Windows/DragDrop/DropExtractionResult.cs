using SmartShift.Core.Drops;

namespace SmartShift.Windows.DragDrop;

public sealed record DropExtractionResult(
    FileDropInspection Inspection,
    ModifierKeySnapshot Modifiers)
{
    public bool HasFileDrop => Inspection.IsValidPayload;
}
