using Microsoft.Win32;

namespace ClipboardFloat.App.Services;

/// <summary>开机自启动管理（HKCU 注册表 Run 启动项）。</summary>
public static class AutoStartService
{
    private const string DefaultRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ClipboardFloat";

    /// <summary>当前是否启用了开机自启动。</summary>
    public static bool IsEnabled(string? runKeyPath = null)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(runKeyPath ?? DefaultRunKeyPath, writable: false);
            return key?.GetValue(ValueName) as string is { Length: > 0 } value;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>启用开机自启动（写入当前 exe 路径，带引号）。</summary>
    public static void Enable(string exePath, string? runKeyPath = null)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(runKeyPath ?? DefaultRunKeyPath);
            key.SetValue(ValueName, $"\"{exePath}\"");
        }
        catch
        {
            // 注册表写入失败（权限等）时静默，用户可在设置页看到未生效
        }
    }

    /// <summary>关闭开机自启动。</summary>
    public static void Disable(string? runKeyPath = null)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(runKeyPath ?? DefaultRunKeyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch
        {
        }
    }
}
