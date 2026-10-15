using System.IO;
using OneBox.Host.Services;

namespace OneBox.Tests;

/// <summary>
/// 便携模式路径：所有数据都在宿主安装目录下，不碰系统用户目录。
///
/// 这条约束值得单独测——一旦有人图省事改回 %LOCALAPPDATA%，卸载残留、
/// 多份安装串数据这些老问题会立刻回来，而单元测试是唯一能在提交前拦住它的地方。
/// </summary>
public class HostPathsTests : IDisposable
{
    private readonly string _root;

    public HostPathsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"onebox-paths-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        HostPaths.DataDirectoryOverride = _root;
    }

    public void Dispose()
    {
        HostPaths.DataDirectoryOverride = null;
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void DataDirectory_HonoursOverride()
    {
        Assert.Equal(_root, HostPaths.DataDirectory);
    }

    [Fact]
    public void WithoutOverride_DataLivesBesideTheExecutable()
    {
        // 默认（无覆盖）时必须落在 exe 同级的 data 下——这是"便携"的全部含义
        HostPaths.DataDirectoryOverride = null;
        try
        {
            var expected = Path.GetFullPath(
                Path.Combine(AppContext.BaseDirectory, HostPaths.DataFolderName));

            Assert.Equal(expected, Path.GetFullPath(HostPaths.DataDirectory));
        }
        finally
        {
            HostPaths.DataDirectoryOverride = _root;
        }
    }

    [Fact]
    public void AllPaths_StayUnderInstallDirectory()
    {
        // 覆盖生效时，每一个运行期路径都必须落在数据目录之内
        foreach (var path in new[]
                 {
                     HostPaths.DataDirectory,
                     HostPaths.PluginsDirectory,
                     HostPaths.PluginDataRoot,
                     HostPaths.LogDirectory,
                     HostPaths.HostLogPath,
                     HostPaths.PluginLogPath("demo"),
                     HostPaths.PluginDataDirectory("demo"),
                     HostPaths.RegistryPath,
                     HostPaths.SettingsPath,
                 })
        {
            Assert.StartsWith(_root, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void NoPath_EscapesTheDataRoot()
    {
        // 便携模式的硬性要求：所有路径都从数据根长出来，不许指向别处
        // （测试自身的临时目录就在 %TEMP% 下，所以判据是"相对数据根"而不是"不在用户目录"）
        var root = Path.GetFullPath(_root) + Path.DirectorySeparatorChar;

        var paths = new[]
        {
            HostPaths.PluginsDirectory,
            HostPaths.PluginDataRoot,
            HostPaths.LogDirectory,
            HostPaths.HostLogPath,
            HostPaths.RegistryPath,
            HostPaths.SettingsPath,
        };

        foreach (var path in paths)
        {
            var full = Path.GetFullPath(path);
            Assert.StartsWith(root, full, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void BundledPlugins_LiveBesideHostExecutable()
    {
        // 内置插件在 exe 旁的 plugins/，属于安装目录而非数据目录
        Assert.Equal(
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "plugins")),
            Path.GetFullPath(HostPaths.BundledPluginsDirectory));
    }

    [Fact]
    public void Directories_AreCreatedOnAccess()
    {
        var plugins = HostPaths.PluginsDirectory;
        var data = HostPaths.PluginDataDirectory("fresh");
        var logs = HostPaths.LogDirectory;

        Assert.True(Directory.Exists(plugins));
        Assert.True(Directory.Exists(data));
        Assert.True(Directory.Exists(logs));
    }
}
