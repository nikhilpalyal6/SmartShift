using System.Runtime.InteropServices;

namespace SmartShift.Windows.DragDrop;

internal static class NativeInput
{
    public const int VkShift = 0x10;
    public const int VkControl = 0x11;
    public const int VkMenu = 0x12;
    public const int VkLButton = 0x01;
    public const int VkMButton = 0x04;

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    public static bool IsDown(int vKey) => (GetAsyncKeyState(vKey) & 0x8000) != 0;

    public static ModifierKeySnapshot CaptureModifiers()
        => new(
            Shift: IsDown(VkShift),
            Control: IsDown(VkControl),
            Alt: IsDown(VkMenu),
            MiddleMouse: IsDown(VkMButton),
            LeftMouse: IsDown(VkLButton));
}
