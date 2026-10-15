using System.IO;
using OneBox.Host.Services;

namespace OneBox.Tests;

/// <summary>
/// 旧版数据目录迁移。改便携后这一步是"用户数据不丢"的唯一保障，
/// 所以几条关键分支都要钉住：真的会搬、只搬一次、不覆盖已有、失败不抛。
/// </summary>
public class LegacyDataMigratorTests : IDisposable
{
    private readonly string _root;

    public LegacyDataMigratorTests()
        => _root = Path.Combine(Path.GetTempPath(), $"onebox-migrate-{Guid.NewGuid():N}");

    public void Dispose()
    {
        HostPaths.DataDirectoryOverride = null;
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private (string Legacy, string Target) Setup()
    {
        var legacy = Path.Combine(_root, "legacy");
        var target = Path.Combine(_root, "target");
        Directory.CreateDirectory(legacy);
        Directory.CreateDirectory(target);
        return (legacy, target);
    }

    [Fact]
    public void WithOverride_DoesNothing()
    {
        // 测试把数据根指到临时目录时，绝不能去碰真实的 %LOCALAPPDATA%
        HostPaths.DataDirectoryOverride = Path.Combine(_root, "data");
        LegacyDataMigrator.MigrateIfNeeded();

        Assert.False(File.Exists(
            Path.Combine(HostPaths.DataDirectory, ".migrated-from-localappdata")));
    }

    [Fact]
    public void Copies_EverythingItRecognises()
    {
        var (legacy, target) = Setup();

        // 旧目录里放齐三类东西：嵌套目录、单个文件、日志
        Directory.CreateDirectory(Path.Combine(legacy, "plugin-state", "textpin"));
        File.WriteAllText(Path.Combine(legacy, "plugin-state", "textpin", "pinned.txt"), "text");
        File.WriteAllText(Path.Combine(legacy, "plugins.json"), "{ plugins");
        Directory.CreateDirectory(Path.Combine(legacy, "logs"));
        File.WriteAllText(Path.Combine(legacy, "logs", "host.log"), "log");

        LegacyDataMigrator.Migrate(legacy, target);

        Assert.Equal("text", File.ReadAllText(
            Path.Combine(target, "plugin-state", "textpin", "pinned.txt")));
        Assert.Equal("{ plugins", File.ReadAllText(Path.Combine(target, "plugins.json")));
        Assert.Equal("log", File.ReadAllText(Path.Combine(target, "logs", "host.log")));
    }

    [Fact]
    public void SecondRun_IsANoOp()
    {
        var (legacy, target) = Setup();
        File.WriteAllText(Path.Combine(legacy, "plugins.json"), "first");

        LegacyDataMigrator.Migrate(legacy, target);
        File.WriteAllText(Path.Combine(target, "plugins.json"), "changed");

        // 已迁移过一次：第二次不能再把旧内容盖回来
        LegacyDataMigrator.Migrate(legacy, target);

        Assert.Equal("changed", File.ReadAllText(Path.Combine(target, "plugins.json")));
    }

    [Fact]
    public void ExistingFiles_AreNotOverwritten()
    {
        var (legacy, target) = Setup();
        File.WriteAllText(Path.Combine(legacy, "plugins.json"), "old");
        File.WriteAllText(Path.Combine(target, "plugins.json"), "new");

        LegacyDataMigrator.Migrate(legacy, target);

        Assert.Equal("new", File.ReadAllText(Path.Combine(target, "plugins.json")));
    }

    [Fact]
    public void MissingLegacyDirectory_StillWritesMarker()
    {
        // 旧目录不存在（全新安装）时不该报错，也只是留个标记表示"无需迁移"
        var target = Path.Combine(_root, "target");
        Directory.CreateDirectory(target);

        LegacyDataMigrator.Migrate(Path.Combine(_root, "nope"), target);

        Assert.True(File.Exists(Path.Combine(target, ".migrated-from-localappdata")));
    }

    [Fact]
    public void SameDirectory_DoesNothing()
    {
        // 旧目录被显式设成和新目录同一个地方时不该自己复制自己
        var dir = Path.Combine(_root, "same");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "plugins.json"), "keep");

        LegacyDataMigrator.Migrate(dir, dir);

        Assert.Equal("keep", File.ReadAllText(Path.Combine(dir, "plugins.json")));
    }

    [Fact]
    public void UnwritableTarget_DoesNotThrow()
    {
        // 迁移失败绝不能拖垮启动：抓不到就写不进文件，这里验证"不抛"
        var legacy = Path.Combine(_root, "legacy");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "plugins.json"), "data");

        var ex = Record.Exception(() =>
            LegacyDataMigrator.Migrate(legacy, Path.Combine(_root, "missing", "deeper")));

        Assert.Null(ex);
    }

    [Fact]
    public void LegacyDirectory_IsUnderLocalAppData()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OneBox");

        Assert.Equal(expected, LegacyDataMigrator.LegacyDirectory);
    }
}
