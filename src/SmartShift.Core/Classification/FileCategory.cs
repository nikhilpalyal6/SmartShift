namespace SmartShift.Core.Classification;

/// <summary>
/// Broad classification category for recognized file types.
/// </summary>
public enum FileCategory
{
    Unknown = 0,
    Image = 1,
    Document = 2,
    Presentation = 3,
    Spreadsheet = 4,
    Archive = 5,
    Other = 6
}
