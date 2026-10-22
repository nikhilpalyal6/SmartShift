using SmartShift.Infrastructure.Output;
using Xunit;

namespace SmartShift.Infrastructure.Tests;

public sealed class OutputPathAllocatorTests
{
    [Fact]
    public void Allocate_AvoidsCollisionAndOriginalPath()
    {
        var root = Path.Combine(Path.GetTempPath(), "smartshift-alloc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var original = Path.Combine(root, "source.png");
        File.WriteAllText(original, "orig");
        var taken = Path.Combine(root, "out.png");
        File.WriteAllText(taken, "taken");
        try
        {
            var allocator = new OutputPathAllocator(root);
            var allocated = allocator.Allocate(original, "out.png");
            Assert.Equal(Path.Combine(root, "out (1).png"), allocated);
            Assert.False(string.Equals(allocated, original, StringComparison.OrdinalIgnoreCase));
            Assert.True(File.Exists(original));
            Assert.Equal("orig", File.ReadAllText(original));
            Assert.Equal("taken", File.ReadAllText(taken));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Allocate_RejectsInvalidNamesAndPathEscape()
    {
        var root = Path.Combine(Path.GetTempPath(), "smartshift-alloc-" + Guid.NewGuid().ToString("N"));
        var allocator = new OutputPathAllocator(root);
        Assert.Throws<ArgumentException>(() => allocator.Allocate("c:\\in.png", "bad:name.png"));
        Assert.Throws<ArgumentException>(() => allocator.Allocate("c:\\in.png", ""));
    }

    [Fact]
    public void GetDefaultRootDirectory_PrioritizesDriveE_WhenPresent()
    {
        var defaultDir = OutputPathAllocator.GetDefaultRootDirectory();
        if (Directory.Exists(@"E:\"))
        {
            Assert.StartsWith(@"E:\", defaultDir, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(Path.Combine(@"E:\SmartShift", "Output"), defaultDir);
        }
        else
        {
            Assert.Contains("SmartShift", defaultDir);
        }
    }
}
