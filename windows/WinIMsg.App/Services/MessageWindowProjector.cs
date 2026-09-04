namespace WinIMsg.App.Services;

public sealed class MessageWindowProjector<T>
{
    private readonly List<T> _allItems = [];
    private readonly int _initialVisibleCount;
    private readonly int _pageSize;
    private int _visibleCount;

    public MessageWindowProjector(int initialVisibleCount = 80, int pageSize = 80)
    {
        if (initialVisibleCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(initialVisibleCount));
        }

        if (pageSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }

        _initialVisibleCount = initialVisibleCount;
        _pageSize = pageSize;
        _visibleCount = initialVisibleCount;
    }

    public IReadOnlyList<T> AllItems => _allItems;

    public int VisibleCount => Math.Min(_visibleCount, _allItems.Count);

    public bool CanLoadOlder => _visibleCount < _allItems.Count;

    public IReadOnlyList<T> VisibleItems
    {
        get
        {
            var visibleCount = VisibleCount;
            var firstVisibleIndex = Math.Max(0, _allItems.Count - visibleCount);
            return _allItems.Skip(firstVisibleIndex).ToList();
        }
    }

    public void ReplaceAll(IEnumerable<T> items, bool resetVisibleWindow)
    {
        _allItems.Clear();
        _allItems.AddRange(items);
        if (resetVisibleWindow)
        {
            ResetToLatest();
        }
        else
        {
            _visibleCount = Math.Min(Math.Max(_visibleCount, _initialVisibleCount), _allItems.Count);
        }
    }

    public IReadOnlyList<T> ResetToLatest()
    {
        _visibleCount = _initialVisibleCount;
        return VisibleItems;
    }

    public IReadOnlyList<T> LoadOlder()
    {
        if (!CanLoadOlder)
        {
            return VisibleItems;
        }

        _visibleCount = Math.Min(_allItems.Count, _visibleCount + _pageSize);
        return VisibleItems;
    }

    public IReadOnlyList<T> GrowVisibleWindow(int count)
    {
        if (count <= 0)
        {
            return VisibleItems;
        }

        _visibleCount = Math.Min(_allItems.Count, _visibleCount + count);
        return VisibleItems;
    }
}
