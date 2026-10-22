namespace SmartShift.Core.Drops;

/// <summary>
/// Inspects candidate file paths without moving, copying, renaming, or deleting them.
/// </summary>
public static class FileDropInspector
{
    public static FileDropInspection Inspect(IEnumerable<string>? paths, IReadOnlyList<string>? availableFormats = null)
    {
        var formats = availableFormats ?? Array.Empty<string>();
        if (paths is null)
        {
            return FileDropInspection.Invalid("Drop payload was null.", formats);
        }

        var list = paths.ToList();
        if (list.Count == 0)
        {
            return FileDropInspection.Invalid("Drop payload contained no file paths.", formats);
        }

        var files = new List<InspectedFile>(list.Count);
        var rejected = new List<string>();

        foreach (var raw in list)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                rejected.Add("Encountered an empty path entry.");
                continue;
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(raw);
            }
            catch (Exception ex)
            {
                rejected.Add($"Could not resolve path '{raw}': {ex.Message}");
                continue;
            }

            var exists = File.Exists(fullPath);
            long? length = null;
            DateTimeOffset? lastWrite = null;
            if (exists)
            {
                var info = new FileInfo(fullPath);
                length = info.Length;
                lastWrite = info.LastWriteTimeUtc;
            }
            else if (Directory.Exists(fullPath))
            {
                rejected.Add($"Rejected directory '{fullPath}'. Phase 0 inspects files only.");
                continue;
            }

            var kind = FileKindClassifier.Classify(fullPath);
            files.Add(new InspectedFile(
                Path: fullPath,
                FileName: System.IO.Path.GetFileName(fullPath),
                Kind: kind,
                Exists: exists,
                LengthBytes: length,
                LastWriteTimeUtc: lastWrite));
        }

        if (files.Count == 0)
        {
            return new FileDropInspection(
                IsValidPayload: false,
                Status: "No inspectable files were present in the drop payload.",
                AvailableFormats: formats,
                Files: files,
                RejectedReasons: rejected);
        }

        var status = files.Any(f => f.Kind != SupportedFileKind.Unsupported)
            ? $"Inspected {files.Count} file(s); {files.Count(f => f.Kind != SupportedFileKind.Unsupported)} supported for later phases."
            : $"Inspected {files.Count} file(s); none are supported image/PDF types.";

        return new FileDropInspection(
            IsValidPayload: true,
            Status: status,
            AvailableFormats: formats,
            Files: files,
            RejectedReasons: rejected);
    }

    /// <summary>
    /// Confirms files still exist with the same length and last-write time. Never mutates files.
    /// </summary>
    public static bool OriginalFilesUnchanged(IEnumerable<InspectedFile> files, out string details)
    {
        foreach (var file in files)
        {
            if (!file.Exists)
            {
                continue;
            }

            if (!File.Exists(file.Path))
            {
                details = $"File missing after inspection: {file.Path}";
                return false;
            }

            var info = new FileInfo(file.Path);
            if (file.LengthBytes is long expectedLength && info.Length != expectedLength)
            {
                details = $"Length changed for {file.Path}";
                return false;
            }

            if (file.LastWriteTimeUtc is DateTimeOffset expectedWrite &&
                info.LastWriteTimeUtc != expectedWrite.UtcDateTime)
            {
                details = $"Last write time changed for {file.Path}";
                return false;
            }
        }

        details = "Original files were not modified.";
        return true;
    }
}
