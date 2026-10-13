using Microsoft.Win32;

namespace OneBox.Plugins.Clipboard.Services;

/// <summary>
/// 只读查询开机自启动状态。
/// 注册/注销 Run 项由宿主统一负责（插件各自写启动项会导致卸载后残留），插件设置页仅展示状态。
/// </summary>
public static class AutoStartService
{
    private const string DefaultRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>宿主是否已启用开机自启动。</summary>
    public static bool IsEnabled(string? valueName = null, string? runKeyPath = null)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(runKeyPath ?? DefaultRunKeyPath, writable: false);
            return key?.GetValue(valueName ?? "OneBox") as string is { Length: > 0 };
        }
        catch
        {
            return false;
        }
    }
}
