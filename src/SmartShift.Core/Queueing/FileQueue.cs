using SmartShift.Core.Classification;

namespace SmartShift.Core.Queueing;

/// <summary>
/// In-memory drop queue. Removing items does not delete files on disk.
/// </summary>
public sealed class FileQueue
{
    private readonly List<ClassifiedFile> _items = [];

    public IReadOnlyList<ClassifiedFile> Items => _items;

    public ClassifiedFile AddOrReplace(ClassifiedFile file)
    {
        var index = _items.FindIndex(i => string.Equals(i.Path, file.Path, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            _items[index] = file;
        }
        else
        {
            _items.Add(file);
        }

        return file;
    }

    public bool Remove(string path)
        => _items.RemoveAll(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase)) > 0;

    public void Clear() => _items.Clear();
}
