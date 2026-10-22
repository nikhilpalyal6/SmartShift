namespace SmartShift.Core.IO;

public interface IOutputPathAllocator
{
    string RootDirectory { get; }

    void EnsureRoot();

    string Allocate(string originalPath, string desiredFileName);
}
