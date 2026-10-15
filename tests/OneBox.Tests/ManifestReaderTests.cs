using System.IO;
using System.Text.Json;
using OneBox.Abstractions;

namespace OneBox.Tests;

/// <summary>插件清单读取与校验：损坏清单要给出可读原因，而不是抛异常。</summary>
public class ManifestReaderTests : IDisposable
{
    private readonly string _dir;

    public ManifestReaderTests()
        => _dir = Path.Combine(Path.GetTempPath(), $"onebox-manifest-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string WritePlugin(string manifestJson, string entryFile = "app.exe")
    {
        var dir = Path.Combine(_dir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, Protocol.ManifestFileName), manifestJson);
        if (entryFile.Length > 0)
            File.WriteAllText(Path.Combine(dir, entryFile), "stub");
        return dir;
    }

    private static string Manifest(string id = "demo", string entry = "app.exe", string name = "示例",
        int protocol = 1, string? icon = null)
        => JsonSerializer.Serialize(new PluginManifest
        {
            Id = id,
            Name = name,
            Entry = entry,
            Icon = icon,
            ProtocolVersion = protocol,
            Description = "测试插件",
        }, Protocol.ManifestJson);

    [Fact]
    public void ValidManifest_Loads()
    {
        var dir = WritePlugin(Manifest());
        var result = ManifestReader.Load(dir);

        Assert.True(result.Success);
        Assert.Equal("demo", result.Manifest!.Id);
        Assert.Equal("示例", result.Manifest.Name);
        Assert.Equal(dir, result.Manifest.Directory);
    }

    [Fact]
    public void MissingManifest_ReportsError()
    {
        var dir = Path.Combine(_dir, "empty");
        Directory.CreateDirectory(dir);

        var result = ManifestReader.Load(dir);

        Assert.False(result.Success);
        Assert.Contains(Protocol.ManifestFileName, result.Error);
    }

    [Fact]
    public void MalformedJson_ReportsError()
    {
        var dir = WritePlugin("{ 这不是 JSON");
        var result = ManifestReader.Load(dir);

        Assert.False(result.Success);
        Assert.Contains("解析失败", result.Error);
    }

    [Theory]
    [InlineData("Demo")]        // 大写
    [InlineData("_demo")]       // 以非字母数字开头
    [InlineData("demo app")]    // 空格
    [InlineData("")]            // 空
    public void InvalidId_Rejected(string id)
    {
        var dir = WritePlugin(Manifest(id: id));
        var result = ManifestReader.Load(dir);

        Assert.False(result.Success);
        Assert.Contains("id", result.Error);
    }

    [Fact]
    public void MissingEntryFile_Rejected()
    {
        var dir = WritePlugin(Manifest(), entryFile: "");
        var result = ManifestReader.Load(dir);

        Assert.False(result.Success);
        Assert.Contains("入口文件不存在", result.Error);
    }

    [Fact]
    public void EntryEscapingPluginDirectory_Rejected()
    {
        // 清单指向插件目录之外的可执行文件时必须被拒绝
        var outside = Path.Combine(_dir, "outside.exe");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(outside, "stub");

        var dir = WritePlugin(Manifest(entry: @"..\outside.exe"));
        var result = ManifestReader.Load(dir);

        Assert.False(result.Success);
        Assert.Contains("插件目录之外", result.Error);
    }

    [Fact]
    public void IncompatibleProtocolVersion_Rejected()
    {
        var dir = WritePlugin(Manifest(protocol: Protocol.Version + 1));
        var result = ManifestReader.Load(dir);

        Assert.False(result.Success);
        Assert.Contains("不兼容", result.Error);
    }

    [Fact]
    public void ZeroProtocolVersion_TreatedAsCurrent()
    {
        // 未声明协议版本时按当前版本处理，便于手写清单
        var dir = WritePlugin(Manifest(protocol: 0));
        var result = ManifestReader.Load(dir);

        Assert.True(result.Success);
    }

    [Fact]
    public void DeclaredIconInsidePluginDirectory_IsKept()
    {
        var dir = WritePlugin(Manifest(icon: "app.ico"));
        File.WriteAllBytes(Path.Combine(dir, "app.ico"), [1, 2, 3]);

        var result = ManifestReader.Load(dir);

        Assert.True(result.Success);
        Assert.Equal("app.ico", result.Manifest!.Icon);
    }

    [Fact]
    public void MissingIconFile_FallsBackInsteadOfFailing()
    {
        // 图标只是装饰：文件不在也不能把插件判定成损坏。
        // 清单字段本身会被清空，宿主转而用 exe 里嵌入的图标。
        var dir = WritePlugin(Manifest(icon: "app.ico"));

        var result = ManifestReader.Load(dir);

        Assert.True(result.Success);
        Assert.Null(result.Manifest!.Icon);
    }

    [Fact]
    public void IconEscapingPluginDirectory_Ignored()
    {
        // 清单不能借图标路径指向插件目录之外，但同样只降级、不判损坏
        var outside = Path.Combine(_dir, "outside.ico");
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(outside, [1, 2, 3]);

        var dir = WritePlugin(Manifest(icon: @"..\outside.ico"));
        var result = ManifestReader.Load(dir);

        Assert.True(result.Success);
        Assert.Null(result.Manifest!.Icon);
    }

    [Fact]
    public void NoIconDeclared_LeavesIconNull()
    {
        var dir = WritePlugin(Manifest());
        var result = ManifestReader.Load(dir);

        Assert.True(result.Success);
        Assert.Null(result.Manifest!.Icon);
    }
}
