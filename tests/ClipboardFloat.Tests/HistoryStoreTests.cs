using System.IO;
using ClipboardFloat.Core;

namespace ClipboardFloat.Tests;

public class HistoryStoreTests : IDisposable
{
    private readonly string _dbPath;

    public HistoryStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"cf-test-{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { }
        try { File.Delete(_dbPath + "-wal"); } catch { }
        try { File.Delete(_dbPath + "-shm"); } catch { }
    }

    private static ClipItem TextItem(string text)
        => ClipItem.Create(ClipKind.Text, text, null, null, null, null, text);

    private static ClipItem ImageItem(byte[] png)
        => ClipItem.Create(ClipKind.Image, null, null, null, png, null, "[图片]");

    [Fact]
    public void UpsertAndLoad_Roundtrip()
    {
        using var store = new HistoryStore(_dbPath);
        var item = TextItem("你好 world");
        store.SaveAdded(item);

        var loaded = store.LoadAll();
        Assert.Single(loaded);
        Assert.Equal(item.Hash, loaded[0].Hash);
        Assert.Equal(item.Text, loaded[0].Text);
        Assert.Equal(ClipKind.Text, loaded[0].Kind);
        Assert.Equal(item.Preview, loaded[0].Preview);
    }

    [Fact]
    public void ImageItem_Roundtrip()
    {
        using var store = new HistoryStore(_dbPath);
        var png = new byte[] { 1, 2, 3, 4, 5, 250, 251, 252 };
        store.SaveAdded(ImageItem(png));

        var loaded = store.LoadAll();
        Assert.Single(loaded);
        Assert.Equal(ClipKind.Image, loaded[0].Kind);
        Assert.Equal(png, loaded[0].ImagePng);
    }

    [Fact]
    public void Upsert_SameHash_ReplacesWithoutDuplicate()
    {
        using var store = new HistoryStore(_dbPath);
        var item = TextItem("same");
        store.SaveAdded(item);
        store.SaveAdded(TextItem("same"));

        Assert.Single(store.LoadAll());
    }

    [Fact]
    public void LoadAll_NewestFirst()
    {
        using var store = new HistoryStore(_dbPath);
        store.SaveAdded(TextItem("first"));
        store.SaveAdded(TextItem("second"));
        store.SaveAdded(TextItem("third"));

        var loaded = store.LoadAll();
        Assert.Equal(3, loaded.Count);
        Assert.Equal("third", loaded[0].Text);
        Assert.Equal("first", loaded[2].Text);
    }

    [Fact]
    public void Delete_RemovesRow()
    {
        using var store = new HistoryStore(_dbPath);
        var item = TextItem("del");
        store.SaveAdded(item);
        store.Delete(item.Hash);
        Assert.Empty(store.LoadAll());
    }

    [Fact]
    public void Clear_RemovesAll()
    {
        using var store = new HistoryStore(_dbPath);
        store.SaveAdded(TextItem("a"));
        store.SaveAdded(TextItem("b"));
        store.Clear();
        Assert.Empty(store.LoadAll());
    }
}
