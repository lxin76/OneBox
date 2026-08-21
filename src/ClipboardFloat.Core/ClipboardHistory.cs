namespace ClipboardFloat.Core;

/// <summary>内存剪贴板历史（最新在前，按内容哈希去重，超出容量裁剪最旧）。</summary>
public sealed class ClipboardHistory
{
    private readonly List<ClipItem> _items = new();
    private readonly Dictionary<string, ClipItem> _byHash = new();

    public int Capacity { get; }

    public ClipboardHistory(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        Capacity = capacity;
    }

    /// <summary>最新在前的只读视图。</summary>
    public IReadOnlyList<ClipItem> Items => _items;

    public int Count => _items.Count;

    /// <summary>
    /// 加入历史：内容已存在则移动到最前（不新增）；否则插入最前并裁剪超容量旧条目。
    /// 返回集合是否发生了变化。
    /// </summary>
    public bool AddOrBump(ClipItem item)
    {
        if (_byHash.TryGetValue(item.Hash, out var existing))
        {
            var index = _items.IndexOf(existing);
            if (index <= 0) return false;
            _items.RemoveAt(index);
            _items.Insert(0, existing);
            return true;
        }

        _items.Insert(0, item);
        _byHash[item.Hash] = item;

        while (_items.Count > Capacity)
        {
            var last = _items[^1];
            _items.RemoveAt(_items.Count - 1);
            _byHash.Remove(last.Hash);
        }
        return true;
    }

    /// <summary>按哈希删除条目。返回是否删除成功。</summary>
    public bool Remove(string hash)
    {
        if (!_byHash.TryGetValue(hash, out var item)) return false;
        _items.Remove(item);
        _byHash.Remove(hash);
        return true;
    }

    public void Clear()
    {
        _items.Clear();
        _byHash.Clear();
    }
}
