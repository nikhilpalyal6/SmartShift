using SmartShift.Core.Drops;
using SmartShift.Core.Logging;
using Xunit;

namespace SmartShift.Core.Tests;

public sealed class FileDropInspectorTests
{
    [Fact]
    public void Inspect_SingleSupportedFile_ExtractsNameAndDoesNotModify()
    {
        using var fixture = TestFiles.Create("photo.png", "png-bytes");
        var inspection = FileDropInspector.Inspect(new[] { fixture.Path });

        Assert.True(inspection.IsValidPayload);
        Assert.Equal(1, inspection.FileCount);
        Assert.EndsWith("photo.png", inspection.Files[0].FileName, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(SupportedFileKind.Image, inspection.Files[0].Kind);
        Assert.True(FileDropInspector.OriginalFilesUnchanged(inspection.Files, out var details));
        Assert.Equal("Original files were not modified.", details);
        Assert.Equal("png-bytes", File.ReadAllText(fixture.Path));
    }

    [Fact]
    public void Inspect_MultipleFiles_CountsSupportedAndUnsupported()
    {
        using var image = TestFiles.Create("a.jpg", "jpg");
        using var pdf = TestFiles.Create("b.pdf", "pdf");
        using var other = TestFiles.Create("notes.txt", "hello");

        var inspection = FileDropInspector.Inspect(new[] { image.Path, pdf.Path, other.Path });

        Assert.True(inspection.IsValidPayload);
        Assert.Equal(3, inspection.FileCount);
        Assert.Equal(2, inspection.SupportedCount);
        Assert.Equal(1, inspection.UnsupportedCount);
        Assert.Contains(inspection.FileNames, name => name.EndsWith("notes.txt", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Inspect_NullOrEmptyPayload_IsInvalid()
    {
        Assert.False(FileDropInspector.Inspect(null).IsValidPayload);
        Assert.False(FileDropInspector.Inspect(Array.Empty<string>()).IsValidPayload);
    }

    [Fact]
    public void Inspect_Directory_IsRejectedWithoutChangingFolder()
    {
        var directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "smartshift-dir-" + Guid.NewGuid().ToString("N")));
        try
        {
            var inspection = FileDropInspector.Inspect(new[] { directory.FullName });
            Assert.False(inspection.IsValidPayload);
            Assert.Empty(inspection.Files);
            Assert.True(Directory.Exists(directory.FullName));
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    [Fact]
    public void Logger_RecordsErrorWithoutThrowing()
    {
        var logger = new DiagnosticLogger();
        logger.Error("test", "boom", new InvalidOperationException("x"));
        var entry = Assert.Single(logger.Snapshot(), e => e.Level == "ERROR");
        Assert.Equal("boom", entry.Message);
        Assert.Contains("InvalidOperationException", entry.Data["exceptionType"]);
    }
}

internal sealed class TestFiles : IDisposable
{
    private TestFiles(string path) => Path = path;

    public string Path { get; }

    public static TestFiles Create(string name, string contents)
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "smartshift-" + Guid.NewGuid().ToString("N") + "-" + name);
        File.WriteAllText(path, contents);
        return new TestFiles(path);
    }

    public void Dispose()
    {
        if (File.Exists(Path))
        {
            File.Delete(Path);
        }
    }
}
