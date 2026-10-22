namespace SmartShift.Windows.DragDrop;

public readonly record struct ModifierKeySnapshot(
    bool Shift,
    bool Control,
    bool Alt,
    bool MiddleMouse,
    bool LeftMouse)
{
    public string Describe()
    {
        var parts = new List<string>();
        if (Shift) parts.Add("Shift");
        if (Control) parts.Add("Ctrl");
        if (Alt) parts.Add("Alt");
        if (LeftMouse) parts.Add("LMB");
        if (MiddleMouse) parts.Add("MMB");
        return parts.Count == 0 ? "(none)" : string.Join("+", parts);
    }
}
