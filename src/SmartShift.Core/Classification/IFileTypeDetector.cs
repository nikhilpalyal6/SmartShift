namespace SmartShift.Core.Classification;

/// <summary>
/// Central contract for universal file-type detection, signature verification, and package validation.
/// </summary>
public interface IFileTypeDetector
{
    ClassifiedFile Classify(string path);
    FileDetectionResult Detect(string path);
}
