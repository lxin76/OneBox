using OneBox.Abstractions;
using OneBox.Host.Services;

namespace OneBox.Tests;

/// <summary>
/// 托盘菜单分层（每个插件一个子菜单）的纯逻辑测试。
/// </summary>
public class TrayMenuBuilderTests
{
    private static TrayMenuItem Command(string id, string text) => new() { Id = id, Text = text };

    private static TrayMenuItem Separator() => new() { Kind = TrayMenuItemKind.Separator };

    private static (string, string, IReadOnlyList<TrayMenuItem>) Plugin(
        string id, string name, params TrayMenuItem[] items)
        => (id, name, items);

    [Fact]
    public void 每个插件生成一个分组()
    {
        var groups = TrayMenuBuilder.Build(
        [
            Plugin("screenshot", "截图", Command("a", "区域截图")),
            Plugin("quicknote", "便签", Command("b", "新建便签")),
        ]);

        Assert.Equal(2, groups.Count);
        Assert.Equal(["screenshot", "quicknote"], groups.Select(g => g.PluginId));
        Assert.Equal(["截图", "便签"], groups.Select(g => g.PluginName));
    }

    [Fact]
    public void 分组内保持插件的声明顺序()
    {
        var groups = TrayMenuBuilder.Build(
        [
            Plugin("s", "截图", Command("a", "区域"), Command("b", "全屏"), Command("c", "滚动")),
        ]);

        var entries = groups[0].Entries.Where(e => !e.IsSeparator).Select(e => e.Item!.Id);
        Assert.Equal(["a", "b", "c"], entries);
    }

    [Fact]
    public void 没有菜单项的插件不产生分组()
    {
        var groups = TrayMenuBuilder.Build(
        [
            Plugin("empty", "空插件"),
            Plugin("s", "截图", Command("a", "区域")),
        ]);

        Assert.Single(groups);
        Assert.Equal("s", groups[0].PluginId);
    }

    [Fact]
    public void 只声明分隔线的插件不产生分组()
    {
        var groups = TrayMenuBuilder.Build([Plugin("s", "截图", Separator(), Separator())]);

        Assert.Empty(groups);
    }

    [Fact]
    public void 插件名为空时回退到插件id()
    {
        var groups = TrayMenuBuilder.Build([Plugin("screenshot", "", Command("a", "区域"))]);

        Assert.Equal("screenshot", groups[0].PluginName);
    }

    [Fact]
    public void 去掉开头的分隔线()
    {
        var entries = TrayMenuBuilder.Sanitize([Separator(), Command("a", "区域")]);

        Assert.Single(entries);
        Assert.False(entries[0].IsSeparator);
    }

    [Fact]
    public void 去掉结尾的分隔线()
    {
        var entries = TrayMenuBuilder.Sanitize([Command("a", "区域"), Separator()]);

        Assert.Single(entries);
        Assert.False(entries[0].IsSeparator);
    }

    [Fact]
    public void 连续分隔线折叠成一条()
    {
        var entries = TrayMenuBuilder.Sanitize(
            [Command("a", "区域"), Separator(), Separator(), Separator(), Command("b", "全屏")]);

        Assert.Equal(3, entries.Count);
        Assert.False(entries[0].IsSeparator);
        Assert.True(entries[1].IsSeparator);
        Assert.False(entries[2].IsSeparator);
    }

    [Fact]
    public void 中间的分隔线被保留()
    {
        var entries = TrayMenuBuilder.Sanitize(
            [Command("a", "区域"), Separator(), Command("b", "设置")]);

        Assert.Equal(3, entries.Count);
        Assert.True(entries[1].IsSeparator);
    }

    [Fact]
    public void 文本为空的项被丢弃()
    {
        var entries = TrayMenuBuilder.Sanitize(
            [Command("a", "区域"), Command("b", "   "), Command("c", "")]);

        Assert.Single(entries);
        Assert.Equal("a", entries[0].Item!.Id);
    }

    [Fact]
    public void 全是空项时分隔线也一并清掉()
    {
        var entries = TrayMenuBuilder.Sanitize([Command("a", ""), Separator()]);

        Assert.Empty(entries);
    }

    [Fact]
    public void 空输入不产生分组()
    {
        Assert.Empty(TrayMenuBuilder.Build([]));
    }

    [Fact]
    public void 分组保留插件的开关与禁用状态()
    {
        var groups = TrayMenuBuilder.Build(
        [
            Plugin("s", "截图",
                new TrayMenuItem { Id = "t", Text = "贴图置顶", Kind = TrayMenuItemKind.Toggle, Checked = true },
                new TrayMenuItem { Id = "d", Text = "不可用", Enabled = false }),
        ]);

        var entries = groups[0].Entries.Where(e => !e.IsSeparator).ToList();
        Assert.Equal(TrayMenuItemKind.Toggle, entries[0].Item!.Kind);
        Assert.True(entries[0].Item!.Checked);
        Assert.False(entries[1].Item!.Enabled);
    }

    [Fact]
    public void 有可点击项时HasCommands为真()
    {
        var groups = TrayMenuBuilder.Build([Plugin("s", "截图", Command("a", "区域"))]);

        Assert.True(groups[0].HasCommands);
    }

    [Fact]
    public void 插件顺序与传入顺序一致()
    {
        // 托盘菜单顺序要稳定，否则每次重建菜单项都会跳动
        var groups = TrayMenuBuilder.Build(
        [
            Plugin("c", "C", Command("1", "x")),
            Plugin("a", "A", Command("2", "y")),
            Plugin("b", "B", Command("3", "z")),
        ]);

        Assert.Equal(["c", "a", "b"], groups.Select(g => g.PluginId));
    }
}
