namespace OneBox.Plugins.Screenshot.Core;

/// <summary>
/// 贴图标注的撤销栈。只有已提交的标注会进栈；
/// 调整颜色/粗细/字号不属于编辑步骤，不进撤销栈。
/// </summary>
public sealed class PinAnnotationHistory
{
    private readonly List<PinAnnotation> _items = [];

    public IReadOnlyList<PinAnnotation> Items => _items;

    public int Count => _items.Count;

    public bool CanUndo => _items.Count > 0;

    public void Add(PinAnnotation annotation)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        _items.Add(annotation);
    }

    /// <summary>移除并返回最后一条标注；没有可撤销项时返回 null。</summary>
    public PinAnnotation? Undo()
    {
        if (_items.Count == 0) return null;

        var last = _items[^1];
        _items.RemoveAt(_items.Count - 1);
        return last;
    }

    public void Clear() => _items.Clear();
}
