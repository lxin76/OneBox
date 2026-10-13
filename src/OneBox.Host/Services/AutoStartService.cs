using Microsoft.Win32;

namespace OneBox.Host.Services;

/// <summary>开机自启动管理（HKCU 注册表 Run 启动项）。仅宿主需要，插件不应自行注册。</summary>
public static class AutoStartService
{
    private const string DefaultRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string DefaultValueName = "OneBox";

    /// <summary>当前是否启用了开机自启动。</summary>
    public static bool IsEnabled(string? valueName = null, string? runKeyPath = null)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(runKeyPath ?? DefaultRunKeyPath, writable: false);
            return key?.GetValue(valueName ?? DefaultValueName) as string is { Length: > 0 };
        }
        catch
        {
            return false;
        }
    }

    /// <summary>启用开机自启动（写入当前 exe 路径，带引号）。</summary>
    public static void Enable(string exePath, string? valueName = null, string? runKeyPath = null)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(runKeyPath ?? DefaultRunKeyPath);
            key.SetValue(valueName ?? DefaultValueName, $"\"{exePath}\"");
        }
        catch
        {
            // 注册表写入失败（权限等）时静默，设置页会显示未生效
        }
    }

    /// <summary>关闭开机自启动。</summary>
    public static void Disable(string? valueName = null, string? runKeyPath = null)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(runKeyPath ?? DefaultRunKeyPath, writable: true);
            key?.DeleteValue(valueName ?? DefaultValueName, throwOnMissingValue: false);
        }
        catch
        {
        }
    }
}
