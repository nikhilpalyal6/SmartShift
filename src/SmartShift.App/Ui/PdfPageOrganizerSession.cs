using System.Collections.ObjectModel;
using System.IO;
using SmartShift.Processing.Pdf;

namespace SmartShift.App.Ui;

public sealed record PageSnapshot(int OriginalPageIndex, int RotationDegrees);

public sealed record SessionSnapshot(
    IReadOnlyList<PageSnapshot> Pages,
    IReadOnlyList<int> SelectedOriginalIndices);

public sealed class PdfPageOrganizerSession
{
    private const int MaxHistory = 50;

    private readonly List<SessionSnapshot> _undoStack = [];
    private readonly List<SessionSnapshot> _redoStack = [];
    private readonly Dictionary<int, PdfOrganizeItemViewModel> _pageRepository = [];

    public string SourceFilePath { get; private set; } = string.Empty;
    public string SourceFileName => Path.GetFileName(SourceFilePath);
    public long FileSizeBytes { get; private set; }
    public int TotalOriginalPages { get; private set; }
    public IReadOnlyList<int> OriginalRotations { get; private set; } = [];
    public IReadOnlyList<(double WidthPt, double HeightPt)> OriginalDimensions { get; private set; } = [];

    public ObservableCollection<PdfOrganizeItemViewModel> Pages { get; } = [];

    public event EventHandler? StateChanged;

    public void Initialize(
        string filePath,
        int pageCount,
        long fileSizeBytes,
        IReadOnlyList<int> rotations,
        IReadOnlyList<(double WidthPt, double HeightPt)> dimensions)
    {
        SourceFilePath = filePath;
        TotalOriginalPages = pageCount;
        FileSizeBytes = fileSizeBytes;
        OriginalRotations = rotations ?? [];
        OriginalDimensions = dimensions ?? [];

        _undoStack.Clear();
        _redoStack.Clear();
        _pageRepository.Clear();
        Pages.Clear();

        for (int i = 0; i < pageCount; i++)
        {
            int rot = (i < OriginalRotations.Count) ? OriginalRotations[i] : 0;
            var dim = (i < OriginalDimensions.Count) ? OriginalDimensions[i] : (595.0, 842.0);
            var item = new PdfOrganizeItemViewModel(i, rot, dim.Item1, dim.Item2);
            _pageRepository[i] = item;
            Pages.Add(item);
        }

        UpdatePositions();
        OnStateChanged();
    }

    public bool HasUnsavedChanges
    {
        get
        {
            if (Pages.Count != TotalOriginalPages)
            {
                return true;
            }

            for (int i = 0; i < Pages.Count; i++)
            {
                if (Pages[i].OriginalPageIndex != i || Pages[i].RotationDegrees != 0)
                {
                    return true;
                }
            }

            return false;
        }
    }

    public int SelectedCount => Pages.Count(p => p.IsSelected);

    public bool CanUndo => _undoStack.Count > 0;

    public bool CanRedo => _redoStack.Count > 0;

    public bool CanMoveLeft
    {
        get
        {
            var selectedIndices = GetSelectedIndices();
            if (selectedIndices.Count == 0 || selectedIndices[0] == 0) return false;
            return true;
        }
    }

    public bool CanMoveRight
    {
        get
        {
            var selectedIndices = GetSelectedIndices();
            if (selectedIndices.Count == 0 || selectedIndices[^1] == Pages.Count - 1) return false;
            return true;
        }
    }

    public bool CanDeleteSelected
    {
        get
        {
            int sel = SelectedCount;
            return sel > 0 && sel < Pages.Count;
        }
    }

    public bool CanExport => Pages.Count > 0;

    private List<int> GetSelectedIndices()
    {
        var list = new List<int>();
        for (int i = 0; i < Pages.Count; i++)
        {
            if (Pages[i].IsSelected) list.Add(i);
        }
        return list;
    }

    private SessionSnapshot CreateSnapshot()
    {
        var pageSnapshots = Pages.Select(p => new PageSnapshot(p.OriginalPageIndex, p.RotationDegrees)).ToList();
        var selectedOriginals = Pages.Where(p => p.IsSelected).Select(p => p.OriginalPageIndex).ToList();
        return new SessionSnapshot(pageSnapshots, selectedOriginals);
    }

    private void PushUndo()
    {
        _undoStack.Add(CreateSnapshot());
        if (_undoStack.Count > MaxHistory)
        {
            _undoStack.RemoveAt(0);
        }
        _redoStack.Clear();
    }

    private void RestoreSnapshot(SessionSnapshot snapshot)
    {
        Pages.Clear();
        var selectedSet = new HashSet<int>(snapshot.SelectedOriginalIndices);

        for (int i = 0; i < snapshot.Pages.Count; i++)
        {
            var ps = snapshot.Pages[i];
            if (!_pageRepository.TryGetValue(ps.OriginalPageIndex, out var item))
            {
                int rot = (ps.OriginalPageIndex < OriginalRotations.Count) ? OriginalRotations[ps.OriginalPageIndex] : 0;
                var dim = (ps.OriginalPageIndex < OriginalDimensions.Count) ? OriginalDimensions[ps.OriginalPageIndex] : (595.0, 842.0);
                item = new PdfOrganizeItemViewModel(ps.OriginalPageIndex, rot, dim.Item1, dim.Item2);
                _pageRepository[ps.OriginalPageIndex] = item;
            }

            item.RotationDegrees = ps.RotationDegrees;
            item.IsSelected = selectedSet.Contains(ps.OriginalPageIndex);
            Pages.Add(item);
        }

        UpdatePositions();
        OnStateChanged();
    }

    public void Undo()
    {
        if (!CanUndo) return;

        var current = CreateSnapshot();
        _redoStack.Add(current);

        int lastIdx = _undoStack.Count - 1;
        var prev = _undoStack[lastIdx];
        _undoStack.RemoveAt(lastIdx);

        RestoreSnapshot(prev);
    }

    public void Redo()
    {
        if (!CanRedo) return;

        var current = CreateSnapshot();
        _undoStack.Add(current);

        int lastIdx = _redoStack.Count - 1;
        var next = _redoStack[lastIdx];
        _redoStack.RemoveAt(lastIdx);

        RestoreSnapshot(next);
    }

    public void SelectAll()
    {
        foreach (var page in Pages)
        {
            page.IsSelected = true;
        }
        OnStateChanged();
    }

    public void ClearSelection()
    {
        foreach (var page in Pages)
        {
            page.IsSelected = false;
        }
        OnStateChanged();
    }

    public void ToggleSelection(int index)
    {
        if (index >= 0 && index < Pages.Count)
        {
            Pages[index].IsSelected = !Pages[index].IsSelected;
            OnStateChanged();
        }
    }

    public void SetSelectionRange(int startIndex, int endIndex)
    {
        int min = Math.Min(startIndex, endIndex);
        int max = Math.Max(startIndex, endIndex);
        for (int i = 0; i < Pages.Count; i++)
        {
            Pages[i].IsSelected = (i >= min && i <= max);
        }
        OnStateChanged();
    }

    public void MovePage(int fromIndex, int toIndex)
    {
        if (fromIndex == toIndex || fromIndex < 0 || fromIndex >= Pages.Count || toIndex < 0 || toIndex >= Pages.Count)
        {
            return;
        }

        PushUndo();
        Pages.Move(fromIndex, toIndex);
        UpdatePositions();
        OnStateChanged();
    }

    public void MoveSelected(int direction)
    {
        var indices = GetSelectedIndices();
        if (indices.Count == 0) return;

        if (direction < 0) // Move Left
        {
            if (indices[0] == 0) return; // Cannot move past start

            PushUndo();
            for (int i = 0; i < indices.Count; i++)
            {
                int idx = indices[i];
                Pages.Move(idx, idx - 1);
            }
        }
        else if (direction > 0) // Move Right
        {
            if (indices[^1] >= Pages.Count - 1) return; // Cannot move past end

            PushUndo();
            for (int i = indices.Count - 1; i >= 0; i--)
            {
                int idx = indices[i];
                Pages.Move(idx, idx + 1);
            }
        }

        UpdatePositions();
        OnStateChanged();
    }

    public void RotateSelected(int deltaDegrees)
    {
        var selected = Pages.Where(p => p.IsSelected).ToList();
        if (selected.Count == 0) return;

        PushUndo();
        foreach (var page in selected)
        {
            page.RotationDegrees = ((page.RotationDegrees + deltaDegrees) % 360 + 360) % 360;
        }
        OnStateChanged();
    }

    public void RotatePage(int index, int deltaDegrees)
    {
        if (index >= 0 && index < Pages.Count)
        {
            PushUndo();
            Pages[index].RotationDegrees = ((Pages[index].RotationDegrees + deltaDegrees) % 360 + 360) % 360;
            OnStateChanged();
        }
    }

    public bool DeleteSelected()
    {
        var selected = Pages.Where(p => p.IsSelected).ToList();
        if (selected.Count == 0 || selected.Count >= Pages.Count)
        {
            return false; // Prevent deleting all pages
        }

        PushUndo();
        foreach (var item in selected)
        {
            Pages.Remove(item);
        }

        UpdatePositions();
        OnStateChanged();
        return true;
    }

    public bool DeletePage(int index)
    {
        if (Pages.Count <= 1 || index < 0 || index >= Pages.Count)
        {
            return false; // Prevent deleting only page
        }

        PushUndo();
        Pages.RemoveAt(index);
        UpdatePositions();
        OnStateChanged();
        return true;
    }

    public void ResetToOriginal()
    {
        if (!HasUnsavedChanges) return;

        PushUndo();
        Pages.Clear();

        for (int i = 0; i < TotalOriginalPages; i++)
        {
            if (!_pageRepository.TryGetValue(i, out var item))
            {
                int rot = (i < OriginalRotations.Count) ? OriginalRotations[i] : 0;
                var dim = (i < OriginalDimensions.Count) ? OriginalDimensions[i] : (595.0, 842.0);
                item = new PdfOrganizeItemViewModel(i, rot, dim.Item1, dim.Item2);
                _pageRepository[i] = item;
            }

            item.RotationDegrees = 0;
            item.IsSelected = false;
            Pages.Add(item);
        }

        UpdatePositions();
        OnStateChanged();
    }

    public IReadOnlyList<PdfPageTransform> GetTransforms()
    {
        return Pages.Select(p => new PdfPageTransform(p.OriginalPageIndex, p.RotationDegrees)).ToList();
    }

    private void UpdatePositions()
    {
        for (int i = 0; i < Pages.Count; i++)
        {
            Pages[i].CurrentPosition = i + 1;
        }
    }

    private void OnStateChanged()
    {
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
