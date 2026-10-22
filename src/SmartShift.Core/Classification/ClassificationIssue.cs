namespace SmartShift.Core.Classification;

public enum ClassificationIssue
{
    None = 0,
    Missing = 1,
    Inaccessible = 2,
    Directory = 3,
    Empty = 4,
    UnknownFormat = 5,
    ExtensionContentMismatch = 6,
    FormatNotSupported = 7
}
