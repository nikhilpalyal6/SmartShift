namespace SmartShift.Processing.PresentationStudio.Import;

public sealed class DocumentImporterCatalog
{
    private readonly List<IDocumentImporter> _importers;

    public DocumentImporterCatalog(IEnumerable<IDocumentImporter>? importers = null)
    {
        _importers = importers?.ToList() ??
        [
            new PlainTextImporter(),
            new MarkdownImporter(),
            new DocxImporter(),
            new PdfImporter()
        ];
    }

    public IReadOnlyList<IDocumentImporter> Importers => _importers;

    public IReadOnlyList<string> SupportedExtensions =>
        _importers.SelectMany(i => i.SupportedExtensions).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public bool IsExtensionSupported(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension)) return false;
        var ext = extension.StartsWith('.') ? extension : "." + extension;
        return SupportedExtensions.Any(e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase));
    }

    public IDocumentImporter GetImporter(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("File path must be provided.", nameof(filePath));
        }

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("File not found.", filePath);
        }

        var ext = Path.GetExtension(filePath);
        if (string.IsNullOrEmpty(ext))
        {
            throw new NotSupportedException("Files without an extension are not supported.");
        }

        // Validate signatures for formats that require binary verification
        ValidateFileSignature(filePath, ext);

        var importer = _importers.FirstOrDefault(i => i.CanImport(filePath));
        if (importer is null)
        {
            throw new NotSupportedException($"The file format '{ext}' is not supported for presentation import. Supported formats are: {string.Join(", ", SupportedExtensions)}.");
        }

        return importer;
    }

    public async Task<ExtractedDocumentContent> ImportDocumentAsync(string filePath, CancellationToken cancellationToken)
    {
        var importer = GetImporter(filePath);
        return await importer.ImportAsync(filePath, cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateFileSignature(string filePath, string ext)
    {
        var fileInfo = new FileInfo(filePath);
        if (fileInfo.Length == 0)
        {
            throw new InvalidOperationException("The file is empty (0 bytes).");
        }

        byte[] header = new byte[Math.Min(16, (int)fileInfo.Length)];
        using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            int read = stream.Read(header, 0, header.Length);
            if (read < 4) return;
        }

        var lowerExt = ext.ToLowerInvariant();
        if (lowerExt == ".pdf")
        {
            // Verify %PDF- signature
            if (header[0] != 0x25 || header[1] != 0x50 || header[2] != 0x44 || header[3] != 0x46)
            {
                throw new InvalidOperationException("The file does not match a valid PDF header signature (%PDF-).");
            }
        }
        else if (lowerExt == ".docx")
        {
            // Verify PK zip archive signature: 0x50, 0x4B, 0x03, 0x04 (or empty zip 0x05, 0x06)
            if (header[0] != 0x50 || header[1] != 0x4B)
            {
                throw new InvalidOperationException("The file does not match a valid Word (DOCX) package signature (PK ZIP).");
            }
        }
        else if (lowerExt is ".txt" or ".text" or ".md" or ".markdown")
        {
            // Verify plain text doesn't contain null bytes in prefix
            if (header.Take(Math.Min(header.Length, 16)).Any(b => b == 0))
            {
                throw new InvalidOperationException("The file appears to contain binary data rather than valid text.");
            }
        }
        else
        {
            throw new NotSupportedException($"The file extension '{ext}' is not supported. SmartShift only supports importing: .txt, .md, .docx, .pdf.");
        }
    }
}

