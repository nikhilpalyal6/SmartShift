namespace SmartShift.Processing.Pdf.Preview;

public sealed class PdfPreviewCache
{
    private readonly int _maxEntries;
    private readonly object _lock = new();
    private readonly Dictionary<string, LinkedListNode<CacheItem>> _map;
    private readonly LinkedList<CacheItem> _list;

    public PdfPreviewCache(int maxEntries = 5)
    {
        _maxEntries = Math.Max(1, maxEntries);
        _map = new Dictionary<string, LinkedListNode<CacheItem>>(StringComparer.OrdinalIgnoreCase);
        _list = new LinkedList<CacheItem>();
    }

    private sealed record CacheItem(string Key, PdfPreviewPageData Data);

    private static string BuildKey(string filePath, int pageIndex, int dpiBucket)
        => $"{filePath}::{pageIndex}::{dpiBucket}";

    public bool TryGet(string filePath, int pageIndex, int dpiBucket, out PdfPreviewPageData? data)
    {
        var key = BuildKey(filePath, pageIndex, dpiBucket);
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _list.Remove(node);
                _list.AddFirst(node);
                data = node.Value.Data;
                return true;
            }
        }

        data = null;
        return false;
    }

    public void Set(string filePath, int pageIndex, int dpiBucket, PdfPreviewPageData data)
    {
        var key = BuildKey(filePath, pageIndex, dpiBucket);
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var existingNode))
            {
                _list.Remove(existingNode);
                _map.Remove(key);
            }
            else if (_map.Count >= _maxEntries)
            {
                var lru = _list.Last;
                if (lru != null)
                {
                    _list.RemoveLast();
                    _map.Remove(lru.Value.Key);
                }
            }

            var item = new CacheItem(key, data);
            var newNode = _list.AddFirst(item);
            _map[key] = newNode;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _map.Clear();
            _list.Clear();
        }
    }
}
