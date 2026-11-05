namespace SmartShift.Core.Capabilities;

/// <summary>
/// Level of in-app visual preview supported for a file type.
/// </summary>
public enum PreviewCapability
{
    None = 0,
    Image = 1,
    Pdf = 2,
    Text = 3
}
