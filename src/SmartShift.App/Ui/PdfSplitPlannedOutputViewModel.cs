namespace SmartShift.App.Ui;

public sealed record PdfSplitPlannedOutputViewModel(
    string FileName,
    string PageRangeDescription,
    int PageCount);
