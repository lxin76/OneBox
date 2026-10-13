using System.Collections.ObjectModel;
using OneBox.Plugins.Clipboard.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace OneBox.Plugins.Clipboard.ViewModels;

/// <summary>一次复制操作请求（由 App 层执行剪贴板写入与粘贴）。</summary>
public sealed record CopyRequest(ClipItem Item, bool AutoPaste, bool PlainText);

/// <summary>悬浮窗数据模型：历史列表 + 搜索过滤 + 复制/粘贴/删除命令 + 持久化回调。</summary>
public partial class FloatingViewModel : ObservableObject
{
    private readonly ClipboardHistory _history;
    private readonly Settings _settings;
    private readonly IHistoryPersister? _persister;
    private readonly List<ClipItemViewModel> _all = new();

    public ObservableCollection<ClipItemViewModel> Items { get; } = new();

    /// <summary>请求复制条目（App 层执行并抑制回灌）。</summary>
    public event Action<CopyRequest>? CopyRequested;

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private int _visibleCount;

    public FloatingViewModel(Settings? settings = null, IHistoryPersister? persister = null)
    {
        _settings = settings ?? new Settings();
        _persister = persister;
        _history = new ClipboardHistory(_settings.MaxHistory);
    }

    /// <summary>加入新捕获的条目；内容重复时移动到最前；新增条目持久化，超容量裁剪同步删除。</summary>
    public void AddItem(ClipItem item)
    {
        if (!_history.AddOrBump(item)) return;

        if (_history.Items[0] == item)
        {
            _persister?.SaveAdded(item);
            var vm = new ClipItemViewModel(item);
            _all.Insert(0, vm);
            while (_all.Count > _history.Capacity)
            {
                var removed = _all[^1];
                _all.RemoveAt(_all.Count - 1);
                _persister?.Delete(removed.Item.Hash);
            }
        }
        else
        {
            // 去重命中：把已有条目移到最前
            var index = FindIndex(item.Hash);
            if (index > 0)
            {
                var vm = _all[index];
                _all.RemoveAt(index);
                _all.Insert(0, vm);
            }
        }
        ApplyFilter();
    }

    private int FindIndex(string hash)
    {
        for (int i = 0; i < _all.Count; i++)
            if (_all[i].Item.Hash == hash) return i;
        return -1;
    }

    private void ApplyFilter()
    {
        Items.Clear();
        var q = SearchText?.Trim();
        foreach (var vm in _all)
        {
            if (string.IsNullOrEmpty(q) || Matches(vm, q)) Items.Add(vm);
        }
        VisibleCount = Items.Count;
    }

    private static bool Matches(ClipItemViewModel vm, string query)
        => vm.Preview.Contains(query, StringComparison.OrdinalIgnoreCase)
           || vm.KindLabel.Contains(query, StringComparison.OrdinalIgnoreCase);

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    // ---- 命令 ----

    /// <summary>复制（原样；是否自动粘贴取决于设置）。</summary>
    [RelayCommand]
    private void Copy(ClipItemViewModel item)
        => CopyRequested?.Invoke(new CopyRequest(item.Item, _settings.AutoPaste, PlainText: false));

    /// <summary>仅复制为纯文本（不粘贴）。</summary>
    [RelayCommand]
    private void CopyPlain(ClipItemViewModel item)
        => CopyRequested?.Invoke(new CopyRequest(item.Item, AutoPaste: false, PlainText: true));

    /// <summary>复制为纯文本并粘贴。</summary>
    [RelayCommand]
    private void PastePlain(ClipItemViewModel item)
        => CopyRequested?.Invoke(new CopyRequest(item.Item, AutoPaste: true, PlainText: true));

    /// <summary>从历史中删除该条目（同步删除磁盘记录）。</summary>
    [RelayCommand]
    private void Delete(ClipItemViewModel item)
    {
        if (_history.Remove(item.Item.Hash))
        {
            _persister?.Delete(item.Item.Hash);
            _all.Remove(item);
            ApplyFilter();
        }
    }

    [RelayCommand]
    private void ClearHistory()
    {
        _history.Clear();
        _persister?.Clear();
        _all.Clear();
        Items.Clear();
        VisibleCount = 0;
    }
}
