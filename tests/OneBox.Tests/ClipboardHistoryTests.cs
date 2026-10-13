using OneBox.Plugins.Clipboard.Core;

namespace OneBox.Tests;

public class ClipboardHistoryTests
{
    private static ClipItem Item(string text)
        => ClipItem.Create(ClipKind.Text, text, null, null, null, null, text);

    [Fact]
    public void AddNew_InsertsAtFront()
    {
        var h = new ClipboardHistory(10);
        h.AddOrBump(Item("a"));
        h.AddOrBump(Item("b"));
        Assert.Equal(2, h.Count);
        Assert.Equal("b", h.Items[0].Text);
        Assert.Equal("a", h.Items[1].Text);
    }

    [Fact]
    public void AddDuplicate_MovesToFront_WithoutDuplicate()
    {
        var h = new ClipboardHistory(10);
        h.AddOrBump(Item("a"));
        h.AddOrBump(Item("b"));
        Assert.True(h.AddOrBump(Item("a"))); // 内容相同 → 哈希相同
        Assert.Equal(2, h.Count);
        Assert.Equal("a", h.Items[0].Text);
        Assert.Equal("b", h.Items[1].Text);
    }

    [Fact]
    public void AddDuplicateAtFront_NoChange()
    {
        var h = new ClipboardHistory(10);
        h.AddOrBump(Item("a"));
        Assert.False(h.AddOrBump(Item("a")));
        Assert.Equal(1, h.Count);
    }

    [Fact]
    public void Capacity_TrimsOldest()
    {
        var h = new ClipboardHistory(3);
        h.AddOrBump(Item("1"));
        h.AddOrBump(Item("2"));
        h.AddOrBump(Item("3"));
        h.AddOrBump(Item("4"));
        Assert.Equal(3, h.Count);
        Assert.Equal("4", h.Items[0].Text);
        Assert.Equal("3", h.Items[1].Text);
        Assert.Equal("2", h.Items[2].Text);
    }

    [Fact]
    public void Remove_ByHash_DeletesItem()
    {
        var h = new ClipboardHistory(10);
        var a = Item("a");
        h.AddOrBump(a);
        h.AddOrBump(Item("b"));

        Assert.True(h.Remove(a.Hash));
        Assert.Equal(1, h.Count);
        Assert.Equal("b", h.Items[0].Text);

        Assert.False(h.Remove("不存在的哈希"));
    }

    [Fact]
    public void Clear_Empties()
    {
        var h = new ClipboardHistory(10);
        h.AddOrBump(Item("a"));
        h.Clear();
        Assert.Equal(0, h.Count);
    }

    [Fact]
    public void DifferentKinds_HaveDifferentHashes()
    {
        var text = ClipItem.Create(ClipKind.Text, "x", null, null, null, null, "x");
        var files = ClipItem.Create(ClipKind.Files, null, null, null, null, new[] { "x" }, "x");
        Assert.NotEqual(text.Hash, files.Hash);
    }
}
