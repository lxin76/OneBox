namespace OneBox.Plugins.Clipboard.Core;

/// <summary>历史变更的持久化接口（由内存历史同步到磁盘存储）。</summary>
public interface IHistoryPersister
{
    /// <summary>新增条目时调用。</summary>
    void SaveAdded(ClipItem item);

    /// <summary>按哈希删除条目时调用。</summary>
    void Delete(string hash);

    /// <summary>清空历史时调用。</summary>
    void Clear();
}
