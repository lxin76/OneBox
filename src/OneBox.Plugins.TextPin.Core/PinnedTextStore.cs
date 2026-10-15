namespace OneBox.Plugins.TextPin.Core;

/// <summary>
/// 置顶文本的落盘读写。存成纯文本文件（pinned.txt）而不是 JSON：
/// 内容本身就是纯文本，用户可以直接用记事本打开、备份或替换。
/// </summary>
public static class PinnedTextStore
{
    public static string Load(string? path = null)
    {
        path ??= AppPaths.TextPath;
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : "";
        }
        catch
        {
            // 读取失败（被占用、权限不足等）时按空内容处理，不影响插件启动
            return "";
        }
    }

    public static void Save(string text, string? path = null)
    {
        path ??= AppPaths.TextPath;
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, text ?? "");
        }
        catch
        {
            // 保存失败不影响主流程
        }
    }
}
