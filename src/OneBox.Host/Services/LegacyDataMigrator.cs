using System.IO;

namespace OneBox.Host.Services;

/// <summary>
/// 从旧版数据目录（%LOCALAPPDATA%\OneBox）到便携目录（&lt;安装目录&gt;\data）的一次性迁移。
///
/// 为什么需要它：旧版本把插件、历史、设置都放在系统用户目录里。改成便携后宿主只看
/// 安装目录下的 data，那些数据不会被自动发现——用户看到的就是"我的剪贴板历史没了"。
/// 所以首次启动时静默搬一次。
///
/// 几条刻意的选择：
///   - 只搬一次，成功后在便携目录留一个标记，之后无论旧目录还在不在都不再搬
///   - 便携目录里已有数据就跳过，避免覆盖用户在新版里新产生的东西
///   - 失败不阻断启动：迁移是尽力而为的补偿，不是宿主能跑起来的前提
///   - 旧目录只读不删：万一搬错了，用户还能自己把东西找回来
/// </summary>
public static class LegacyDataMigrator
{
    /// <summary>迁移完成标记。</summary>
    private const string MarkerFileName = ".migrated-from-localappdata";

    /// <summary>旧版数据目录（仅用于迁移，宿主运行期不再使用）。</summary>
    public static string LegacyDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OneBox");

    /// <summary>需要迁移的条目。</summary>
    private static readonly string[] Entries =
    [
        "plugins",
        "plugin-state",
        "logs",
        "plugins.json",
        "settings.json",
    ];

    /// <summary>
    /// 首次启动时把旧目录的数据搬进便携目录。重复调用安全。
    /// </summary>
    public static void MigrateIfNeeded()
    {
        // 测试用 DataDirectoryOverride 指到别处时不应触发迁移
        if (HostPaths.DataDirectoryOverride is { Length: > 0 }) return;

        Migrate(LegacyDirectory, HostPaths.DataDirectory);
    }

    /// <summary>
    /// 把 <paramref name="legacy"/> 里缺失的条目搬到 <paramref name="target"/>。
    /// 抽成显式路径的形态，测试才能用两个临时目录把每条分支都走一遍。
    /// 任何失败都只记日志，不向上抛——迁移是尽力而为的补偿。
    /// </summary>
    internal static void Migrate(string legacy, string target)
    {
        try
        {
            var marker = Path.Combine(target, MarkerFileName);
            if (File.Exists(marker)) return;

            // 旧目录就是新目录（例如被显式设成同一个地方）时无需迁移
            if (Directory.Exists(legacy) && !IsSameDirectory(legacy, target))
                CopyMissing(legacy, target);

            Directory.CreateDirectory(target);
            File.WriteAllText(marker,
                $"已从 {legacy} 迁移于 {DateTime.Now:O}{Environment.NewLine}");
        }
        catch (Exception ex)
        {
            HostLog.Write($"旧数据迁移失败（不影响启动）: {ex.Message}");
        }
    }

    /// <summary>把旧目录里"新目录还没有"的条目复制过来，已存在的保持不动。</summary>
    private static void CopyMissing(string source, string target)
    {
        Directory.CreateDirectory(target);
        var moved = 0;

        foreach (var name in Entries)
        {
            var from = Path.Combine(source, name);
            var to = Path.Combine(target, name);

            if (Directory.Exists(from))
            {
                if (Directory.Exists(to)) continue;
                CopyDirectory(from, to);
                moved++;
            }
            else if (File.Exists(from))
            {
                if (File.Exists(to)) continue;
                File.Copy(from, to);
                moved++;
            }
        }

        if (moved > 0)
            HostLog.Write($"已从旧数据目录迁移 {moved} 项：{source} → {target}");
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: false);
        }
    }

    private static bool IsSameDirectory(string a, string b)
        => string.Equals(
            Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
}
