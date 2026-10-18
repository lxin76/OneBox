using Microsoft.Win32;

namespace OneBox.Host.Services;

/// <summary>
/// 把「OneBox 工具箱」写进资源管理器的右键菜单（桌面背景 + 文件夹空白处）。
///
/// 只放一个入口：右键 → 打开工具箱，由工具箱再去管各个小工具。
/// 之前把每个小工具也铺进右键菜单，菜单太长，而且一级项还容易被当成"点了没反应"。
///
/// 用注册表而不是常驻的 shell 扩展：这里只要一个静态菜单项，注册表就够，
/// 不必往资源管理器里塞一个 DLL——那种扩展一出问题会拖累整个资源管理器。
/// 菜单项点击只能执行命令行，所以它再启动一次 OneBox.exe 并带上 --toolbox，
/// 由单实例闸门转交给正在运行的实例。
///
/// 只写 HKCU：不需要管理员权限，只影响当前用户，取消勾选时也删得干净。
/// </summary>
public static class ContextMenuRegistrar
{
    /// <summary>菜单项在注册表里的键名（写入与清理都认它）。</summary>
    private const string MenuKeyName = "OneBoxHost.Toolbox";

    /// <summary>菜单项显示的文字。</summary>
    private const string MenuText = "OneBox 工具箱";

    /// <summary>旧版本写入的键：父容器，以及各小工具的子菜单项，统一按前缀清掉。</summary>
    private const string LegacyParentKeyName = "OneBox";
    private const string LegacyKeyPrefix = "OneBoxHost.";

    /// <summary>桌面空白处与文件夹空白处的右键菜单注册位置（每用户）。</summary>
    private static readonly string[] MenuRoots =
    [
        @"Software\Classes\DesktopBackground\Shell",
        @"Software\Classes\Directory\Background\shell",
    ];

    /// <summary>按宿主设置同步：勾选了写入，没勾选移除。所有异常都只记日志，不影响宿主运行。</summary>
    public static void Sync(PluginRegistry? registry)
    {
        try
        {
            Apply(registry?.Settings.AddToContextMenu == true, Environment.ProcessPath);
        }
        catch (Exception ex)
        {
            // 写注册表可能被安全软件拦下：右键菜单只是便利功能，失败了不该影响别的
            HostLog.Write($"同步右键菜单失败: {ex.Message}");
        }
    }

    /// <summary>写入或移除右键菜单。</summary>
    public static void Apply(bool enabled, string? hostExePath)
    {
        if (!enabled || string.IsNullOrWhiteSpace(hostExePath))
        {
            Remove();
            return;
        }

        // 先清干净再写：菜单内容随版本变过（早期还有各小工具与设置项），
        // 不清会把上一版的残项一起留在右键菜单里
        Remove();

        foreach (var root in MenuRoots)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey($@"{root}\{MenuKeyName}", writable: true);
                if (key is null) continue;

                key.SetValue(null, MenuText);
                key.SetValue("Icon", hostExePath);

                using var commandKey = key.CreateSubKey("command", writable: true);
                commandKey?.SetValue(null, HostRequest.BuildSwitchCommandLine(hostExePath, HostRequest.Toolbox));
            }
            catch (Exception ex)
            {
                HostLog.Write($"写入右键菜单失败（{root}）: {ex.Message}");
            }
        }
    }

    /// <summary>移除本程序写入的右键菜单项；不存在时什么也不做。</summary>
    public static void Remove()
    {
        foreach (var root in MenuRoots)
        {
            try
            {
                using var shell = Registry.CurrentUser.OpenSubKey(root, writable: true);
                if (shell is null) continue;

                foreach (var name in shell.GetSubKeyNames())
                {
                    var mine = name.StartsWith(LegacyKeyPrefix, StringComparison.Ordinal)
                               || string.Equals(name, LegacyParentKeyName, StringComparison.Ordinal);

                    if (mine) shell.DeleteSubKeyTree(name, throwOnMissingSubKey: false);
                }
            }
            catch (Exception ex)
            {
                HostLog.Write($"移除右键菜单失败（{root}）: {ex.Message}");
            }
        }
    }
}
