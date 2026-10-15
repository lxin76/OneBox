using System.IO;
using OneBox.Plugins.TextPin.Core;

namespace OneBox.Tests;

/// <summary>置顶文本的落盘：存的是纯文本文件，用户可直接用记事本打开。</summary>
public class PinnedTextStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public PinnedTextStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"textpin-store-{Guid.NewGuid():N}");
        _path = Path.Combine(_dir, "pinned.txt");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Load_MissingFile_ReturnsEmpty()
    {
        Assert.Equal("", PinnedTextStore.Load(_path));
    }

    [Fact]
    public void SaveThenLoad_RoundtripsExactly()
    {
        const string text = "第一行\r\n第二行\n\t带缩进";
        PinnedTextStore.Save(text, _path);

        // 存储层不做换行归一（那是 PlainText 的职责），原样进出
        Assert.Equal(text, PinnedTextStore.Load(_path));
    }

    [Fact]
    public void Save_CreatesDirectory()
    {
        var nested = Path.Combine(_dir, "a", "b", "pinned.txt");
        PinnedTextStore.Save("x", nested);
        Assert.True(File.Exists(nested));
    }

    [Fact]
    public void Save_NullBecomesEmpty()
    {
        PinnedTextStore.Save(null!, _path);
        Assert.Equal("", PinnedTextStore.Load(_path));
    }

    [Fact]
    public void Load_UnreadablePath_ReturnsEmptyInsteadOfThrowing()
    {
        // 路径指向一个目录：读取失败也不能把插件拖崩
        Directory.CreateDirectory(_dir);
        Assert.Equal("", PinnedTextStore.Load(_dir));
    }
}
