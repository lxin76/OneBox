using OneBox.Abstractions;
using OneBox.Host.Services;
using OneBox.Host.ViewModels;

namespace OneBox.Tests;

/// <summary>
/// 热键按插件分组的纯逻辑测试。
///
/// 分组的价值全在"归属清晰"上：一旦某个热键被分到错误的组，
/// 用户会去改一个无关的小工具。这里把分组、排序、筛选的规则逐条钉死。
/// </summary>
public class HotkeyGroupBuilderTests
{
    private static PluginHotkeyBinding Binding(
        string combination,
        string actionId = "act",
        string description = "",
        bool registered = true,
        string? error = null)
        => new()
        {
            PluginId = "p",
            ActionId = actionId,
            Combination = combination,
            Description = description,
            Registered = registered,
            Error = error,
        };

    private static HotkeyGroupSource Source(
        string id, string name, bool running, params PluginHotkeyBinding[] hotkeys)
        => new() { PluginId = id, PluginName = name, Running = running, Hotkeys = hotkeys };

    [Fact]
    public void 每个插件生成一个分组()
    {
        var groups = HotkeyGroupBuilder.Build(
        [
            Source("textpin", "置顶文本", true, Binding("Ctrl+Alt+V")),
            Source("screenshot", "截图", true, Binding("Ctrl+Alt+A")),
        ]);

        Assert.Equal(2, groups.Count);
        Assert.Equal(["screenshot", "textpin"], groups.Select(g => g.PluginId));
        Assert.Equal(["截图", "置顶文本"], groups.Select(g => g.PluginName));
    }

    [Fact]
    public void 没有热键的插件不产生分组()
    {
        var groups = HotkeyGroupBuilder.Build(
        [
            Source("empty", "空插件", true),
            Source("textpin", "置顶文本", true, Binding("Ctrl+Alt+V")),
        ]);

        Assert.Single(groups);
        Assert.Equal("textpin", groups[0].PluginId);
    }

    [Fact]
    public void 组内按组合键排序()
    {
        var groups = HotkeyGroupBuilder.Build(
        [
            Source("textpin", "置顶文本", true,
                Binding("Ctrl+Alt+X"), Binding("Ctrl+Alt+C"), Binding("Ctrl+Alt+K")),
        ]);

        Assert.Equal(
            ["Ctrl+Alt+C", "Ctrl+Alt+K", "Ctrl+Alt+X"],
            groups[0].Items.Select(i => i.Combination));
    }

    [Fact]
    public void 组合键相同时按动作标识排序()
    {
        var groups = HotkeyGroupBuilder.Build(
        [
            Source("p", "插件", true,
                Binding("Ctrl+Alt+V", "zeta"), Binding("Ctrl+Alt+V", "alpha")),
        ]);

        Assert.Equal(["alpha", "zeta"], groups[0].Items.Select(i => i.ActionId));
    }

    [Fact]
    public void 热键不会跨插件串组()
    {
        var groups = HotkeyGroupBuilder.Build(
        [
            Source("a", "甲", true, Binding("Ctrl+Alt+1")),
            Source("b", "乙", true, Binding("Ctrl+Alt+2")),
        ]);

        var a = groups.Single(g => g.PluginId == "a");
        var b = groups.Single(g => g.PluginId == "b");

        Assert.Equal(["Ctrl+Alt+1"], a.Items.Select(i => i.Combination));
        Assert.Equal(["Ctrl+Alt+2"], b.Items.Select(i => i.Combination));
    }

    [Fact]
    public void 缺少说明时回退到动作标识()
    {
        var groups = HotkeyGroupBuilder.Build(
        [
            Source("p", "插件", true, Binding("Ctrl+Alt+V", "toggle", description: "")),
        ]);

        Assert.Equal("toggle", groups[0].Items[0].Description);
    }

    [Fact]
    public void 记录注册失败的原因()
    {
        var groups = HotkeyGroupBuilder.Build(
        [
            Source("p", "插件", true, Binding("Ctrl+Alt+V", "a", registered: false, error: "已被占用")),
        ]);

        var row = groups[0].Items[0];
        Assert.False(row.Registered);
        Assert.Equal("已被占用", row.Error);
    }

    [Fact]
    public void 保留插件的运行状态()
    {
        var groups = HotkeyGroupBuilder.Build(
        [
            Source("p", "插件", false, Binding("Ctrl+Alt+V")),
        ]);

        Assert.False(groups[0].Running);
    }

    // ---- 界面模型：筛选与统计 ----

    /// <summary>分组头图标在筛选测试里无关紧要，给一张空位图即可。</summary>
    private static readonly System.Windows.Media.ImageSource NoIcon =
        System.Windows.Media.Imaging.BitmapSource.Create(
            1, 1, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, new byte[4], 4);

    private static HotkeyGroupViewModel Group(bool running = true, params PluginHotkeyBinding[] hotkeys)
        => new(HotkeyGroupBuilder.Build([Source("textpin", "置顶文本", running, hotkeys)])[0], NoIcon);

    [Fact]
    public void 分组默认展开()
    {
        Assert.True(Group(true, Binding("Ctrl+Alt+V")).IsExpanded);
    }

    [Fact]
    public void 统计已生效与未生效的数量()
    {
        var vm = Group(true,
            Binding("Ctrl+Alt+V"),
            Binding("Ctrl+Alt+C", registered: false, error: "被占用"),
            Binding("Ctrl+Alt+X"));

        Assert.Equal(2, vm.RegisteredCount);
        Assert.Equal(1, vm.FailedCount);
        Assert.Contains("未生效", vm.CountText);

        // 注册失败但组合键不重复 = 被平台外的程序占用，与"插件间冲突"是两回事：
        // 后者可以自己改键解决，健康状态必须区分开
        Assert.Equal(0, vm.ConflictCount);
        Assert.Equal("有未生效", vm.HealthText);
    }

    [Fact]
    public void 注册失败且组合键重复时按冲突报告()
    {
        var groups = HotkeyGroupBuilder.Build(
        [
            Source("textpin", "置顶文本", true,
                Binding("Ctrl+Alt+X", "capture", "抓取"),
                Binding("Ctrl+Alt+X", "toggle", "显示/隐藏", registered: false, error: "已被其他插件占用")),
        ]);
        var vm = new HotkeyGroupViewModel(groups[0], NoIcon);

        // 两条声明撞同一个组合键，两边都算冲突（用户改任意一条都能解决）
        Assert.Equal(2, vm.ConflictCount);
        Assert.Equal("有冲突", vm.HealthText);
        Assert.Contains("改键", vm.Subtitle);
    }

    [Fact]
    public void 全部生效时健康状态为正常()
    {
        var vm = Group(true, Binding("Ctrl+Alt+V"));

        Assert.Equal("正常", vm.HealthText);
        Assert.Equal("全部已生效", vm.Subtitle);
    }

    [Fact]
    public void 插件未运行时提示热键已让出()
    {
        var vm = Group(false, Binding("Ctrl+Alt+V"));

        Assert.Equal("未运行", vm.HealthText);
        Assert.Contains("让出", vm.Subtitle);
    }

    [Fact]
    public void 按插件名筛选时整组保留()
    {
        var vm = Group(true, Binding("Ctrl+Alt+V"), Binding("Ctrl+Alt+C"));

        Assert.True(vm.ApplyFilter("置顶"));
        Assert.Equal(2, vm.Items.Count);
    }

    [Fact]
    public void 按插件id筛选时整组保留()
    {
        var vm = Group(true, Binding("Ctrl+Alt+V"), Binding("Ctrl+Alt+C"));

        Assert.True(vm.ApplyFilter("textpin"));
        Assert.Equal(2, vm.Items.Count);
    }

    [Fact]
    public void 按组合键筛选时只留下命中的行()
    {
        var vm = Group(true,
            Binding("Ctrl+Alt+V", "toggle", "显示/隐藏文本框"),
            Binding("Ctrl+Alt+C", "copy", "复制到剪贴板"));

        Assert.True(vm.ApplyFilter("Alt+C"));
        Assert.Single(vm.Items);
        Assert.Equal("Ctrl+Alt+C", vm.Items[0].Combination);
    }

    [Fact]
    public void 按用途筛选时命中()
    {
        var vm = Group(true,
            Binding("Ctrl+Alt+V", "toggle", "显示/隐藏文本框"),
            Binding("Ctrl+Alt+C", "copy", "复制到剪贴板"));

        Assert.True(vm.ApplyFilter("剪贴板"));
        Assert.Single(vm.Items);
        Assert.Equal("Ctrl+Alt+C", vm.Items[0].Combination);
    }

    [Fact]
    public void 无匹配时整组被隐藏()
    {
        var vm = Group(true, Binding("Ctrl+Alt+V", "toggle", "显示/隐藏"));

        Assert.False(vm.ApplyFilter("完全不相干的关键字"));
        Assert.Empty(vm.Items);
    }

    [Fact]
    public void 清空筛选后恢复全部行()
    {
        var vm = Group(true,
            Binding("Ctrl+Alt+V", "toggle", "显示/隐藏"),
            Binding("Ctrl+Alt+C", "copy", "复制到剪贴板"));

        vm.ApplyFilter("剪贴板");
        Assert.Single(vm.Items);

        vm.ApplyFilter("");
        Assert.Equal(2, vm.Items.Count);
    }

    [Fact]
    public void 筛选不影响分组统计()
    {
        var vm = Group(true, Binding("Ctrl+Alt+V"), Binding("Ctrl+Alt+C"));

        vm.ApplyFilter("Alt+C");

        // 组头徽标仍应报告这个插件的真实热键总数，而不是筛选后的行数
        Assert.Single(vm.Items);
        Assert.Equal(2, vm.AllItems.Count);
        Assert.Contains("2 个热键", vm.CountText);
    }

    [Fact]
    public void 筛选大小写不敏感()
    {
        var vm = Group(true, Binding("Ctrl+Alt+V", "toggle", "显示/隐藏文本框"));

        Assert.True(vm.ApplyFilter("CTRL+ALT+V"));
        Assert.Single(vm.Items);
    }
}

/// <summary>
/// 跨插件热键冲突检测。
///
/// 这是"添加冲突检测"的核心：冲突的本质是**同一个组合键被两条热键声明占用**，
/// 而宿主统一注册时只有先来的那条能成功。判定必须覆盖三类易错情形：
/// 写法不同但语义相同（大小写/修饰键顺序）、同一插件内部自撞、以及不该误报的情形。
/// </summary>
public class HotkeyConflictDetectionTests
{
    private static PluginHotkeyBinding Binding(
        string combination, string actionId = "act", string description = "",
        bool registered = true, string? error = null)
        => new()
        {
            PluginId = "p",
            ActionId = actionId,
            Combination = combination,
            Description = description,
            Registered = registered,
            Error = error,
        };

    private static HotkeyGroupSource Source(
        string id, string name, bool running, params PluginHotkeyBinding[] hotkeys)
        => new() { PluginId = id, PluginName = name, Running = running, Hotkeys = hotkeys };

    /// <summary>取某个插件分组里指定组合键的那一行（该组合键在此分组内唯一时使用）。</summary>
    private static HotkeyRow Row(IReadOnlyList<HotkeyGroup> groups, string pluginId, string combination)
        => groups.Single(g => g.PluginId == pluginId)
                 .Items.Single(i => i.Combination == combination);

    /// <summary>取某个插件分组里指定动作的那一行；同组合键的多条声明用这个区分。</summary>
    private static HotkeyRow RowByAction(IReadOnlyList<HotkeyGroup> groups, string pluginId, string actionId)
        => groups.Single(g => g.PluginId == pluginId)
                 .Items.Single(i => i.ActionId == actionId);

    [Fact]
    public void 无重复时不标记冲突()
    {
        var groups = HotkeyGroupBuilder.Build(
        [
            Source("textpin", "置顶文本", true,
                Binding("Ctrl+Alt+V", "toggle"), Binding("Ctrl+Alt+P", "paste")),
            Source("screenshot", "截图", true, Binding("Ctrl+Alt+A", "region")),
        ]);

        Assert.All(groups.SelectMany(g => g.Items), r => Assert.False(r.HasConflict));
        Assert.All(groups.SelectMany(g => g.Items), r => Assert.Null(r.Conflict));
    }

    [Fact]
    public void 跨插件组合键重复时双方都被标记()
    {
        var groups = HotkeyGroupBuilder.Build(
        [
            Source("textpin", "置顶文本", true, Binding("Ctrl+Alt+X", "capture", "抓取选中文字")),
            Source("screenshot", "截图", true, Binding("Ctrl+Alt+X", "region", "区域截图")),
        ]);

        var a = Row(groups, "textpin", "Ctrl+Alt+X");
        var b = Row(groups, "screenshot", "Ctrl+Alt+X");

        // 两边都要高亮：用户在任意一侧改键都应当看到问题
        Assert.True(a.HasConflict);
        Assert.True(b.HasConflict);
    }

    [Fact]
    public void 冲突文案点名对方的小工具与用途()
    {
        var groups = HotkeyGroupBuilder.Build(
        [
            Source("textpin", "置顶文本", true, Binding("Ctrl+Alt+X", "capture", "抓取选中文字")),
            Source("screenshot", "截图", true, Binding("Ctrl+Alt+X", "region", "区域截图")),
        ]);

        var row = Row(groups, "textpin", "Ctrl+Alt+X");

        Assert.Contains("截图", row.Conflict);
        Assert.Contains("区域截图", row.Conflict);
        Assert.Contains("其他小工具", row.Conflict);
    }

    [Fact]
    public void 冲突列表给出对方的插件id与同插件标记()
    {
        var groups = HotkeyGroupBuilder.Build(
        [
            Source("textpin", "置顶文本", true, Binding("Ctrl+Alt+X", "capture", "抓取")),
            Source("screenshot", "截图", true, Binding("Ctrl+Alt+X", "region", "区域截图")),
        ]);

        var peer = Row(groups, "textpin", "Ctrl+Alt+X").ConflictWith.Single();

        Assert.Equal("screenshot", peer.PluginId);
        Assert.Equal("截图", peer.PluginName);
        Assert.False(peer.SamePlugin);
        Assert.Equal("截图 · 区域截图", peer.Describe());
    }

    [Fact]
    public void 同一插件内部重复也算冲突()
    {
        var groups = HotkeyGroupBuilder.Build(
        [
            Source("textpin", "置顶文本", true,
                Binding("Ctrl+Alt+X", "capture", "抓取选中文字"),
                Binding("Ctrl+Alt+X", "toggle", "显示/隐藏文本框")),
        ]);

        var capture = RowByAction(groups, "textpin", "capture");
        var toggle = RowByAction(groups, "textpin", "toggle");

        Assert.True(capture.HasConflict);
        Assert.Contains("本小工具", capture.Conflict);
        Assert.True(capture.ConflictWith.Single().SamePlugin);

        // 两边都要高亮，否则用户只看到一条被标红，不知道同组的另一条也有问题
        Assert.True(toggle.HasConflict);
        Assert.True(toggle.ConflictWith.Single().SamePlugin);
    }

    [Fact]
    public void 大小写与修饰键顺序不同仍判定为冲突()
    {
        // 这是冲突检测最容易漏掉的一类：写法不同、语义相同。
        // 宿主注册前会 Normalize，因此判定也必须基于规范化结果。
        var groups = HotkeyGroupBuilder.Build(
        [
            Source("textpin", "置顶文本", true, Binding("Ctrl+Alt+X", "capture", "抓取")),
            Source("screenshot", "截图", true, Binding("alt+ctrl+x", "region", "区域截图")),
        ]);

        Assert.True(Row(groups, "textpin", "Ctrl+Alt+X").HasConflict);
        Assert.True(Row(groups, "screenshot", "alt+ctrl+x").HasConflict);
    }

    [Fact]
    public void 三个插件撞同一个键时每行都列出另外两个()
    {
        var groups = HotkeyGroupBuilder.Build(
        [
            Source("a", "甲", true, Binding("Ctrl+Alt+X", "x", "甲的动作")),
            Source("b", "乙", true, Binding("Ctrl+Alt+X", "x", "乙的动作")),
            Source("c", "丙", true, Binding("Ctrl+Alt+X", "x", "丙的动作")),
        ]);

        var row = Row(groups, "a", "Ctrl+Alt+X");

        Assert.Equal(2, row.ConflictWith.Count);
        Assert.Contains("乙", row.Conflict);
        Assert.Contains("丙", row.Conflict);
    }

    [Fact]
    public void 无法解析的组合键不参与冲突判定()
    {
        // 解析不了的组合本来就注册不上（宿主会报"无法解析"），
        // 不该再额外报一个冲突把用户引到错误的方向
        var groups = HotkeyGroupBuilder.Build(
        [
            Source("a", "甲", true, Binding("这不是热键", "x", "甲")),
            Source("b", "乙", true, Binding("这不是热键", "x", "乙")),
        ]);

        Assert.All(groups.SelectMany(g => g.Items), r => Assert.False(r.HasConflict));
    }

    [Fact]
    public void 冲突与注册失败可以并存且各自保留原因()
    {
        // 真实场景：先注册的插件占住了键，后注册的插件注册失败。
        // 后者的 Error 说明"为什么没生效"，Conflict 说明"和谁撞了"，两者都要在。
        var groups = HotkeyGroupBuilder.Build(
        [
            Source("textpin", "置顶文本", true, Binding("Ctrl+Alt+X", "capture", "抓取")),
            Source("screenshot", "截图", true,
                Binding("Ctrl+Alt+X", "region", "区域截图", registered: false,
                    error: "热键 Ctrl+Alt+X 已被其他插件占用")),
        ]);

        var failed = Row(groups, "screenshot", "Ctrl+Alt+X");

        Assert.False(failed.Registered);
        Assert.NotNull(failed.Error);
        Assert.True(failed.HasConflict);
    }

    // ---- 界面模型：高亮与统计 ----

    private static readonly System.Windows.Media.ImageSource NoIcon =
        System.Windows.Media.Imaging.BitmapSource.Create(
            1, 1, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, new byte[4], 4);

    private static IReadOnlyList<HotkeyGroupViewModel> Groups(params HotkeyGroupSource[] sources)
        => [.. HotkeyGroupBuilder.Build(sources).Select(g => new HotkeyGroupViewModel(g, NoIcon))];

    [Fact]
    public void 冲突行状态文本为冲突()
    {
        var vm = Groups(
            Source("textpin", "置顶文本", true, Binding("Ctrl+Alt+X", "capture", "抓取")),
            Source("screenshot", "截图", true, Binding("Ctrl+Alt+X", "region", "区域截图")))[0];

        var row = vm.AllItems.Single(i => i.Combination == "Ctrl+Alt+X");

        Assert.True(row.HasConflict);
        Assert.Equal("冲突", row.Status);
    }

    [Fact]
    public void 冲突行使用红色而非橙色的状态色()
    {
        var groups = Groups(
            Source("textpin", "置顶文本", true, Binding("Ctrl+Alt+X", "capture", "抓取")),
            Source("screenshot", "截图", true, Binding("Ctrl+Alt+X", "region", "区域截图")));

        var conflictRow = groups[0].AllItems.Single(i => i.Combination == "Ctrl+Alt+X");
        var okRow = groups[1].AllItems.Single(i => i.Combination == "Ctrl+Alt+X");

        // 冲突与"已生效/未生效"必须用不同颜色，否则用户分不清该不该改键
        Assert.Equal(HotkeyBrushes.Error, conflictRow.StatusBrush);
        Assert.Equal(HotkeyBrushes.Error, okRow.StatusBrush);
    }

    [Fact]
    public void 无冲突的行保持正常配色()
    {
        var vm = Groups(Source("textpin", "置顶文本", true, Binding("Ctrl+Alt+V", "toggle")))[0];

        var row = vm.AllItems[0];

        Assert.False(row.HasConflict);
        Assert.Equal(HotkeyBrushes.Ok, row.StatusBrush);
        Assert.Equal("已生效", row.Status);
    }

    [Fact]
    public void 分组统计冲突数并在徽标与副标题里体现()
    {
        var vm = Groups(
            Source("textpin", "置顶文本", true,
                Binding("Ctrl+Alt+X", "capture", "抓取"),
                Binding("Ctrl+Alt+V", "toggle", "显示/隐藏")),
            Source("screenshot", "截图", true, Binding("Ctrl+Alt+X", "region", "区域截图")))[1];

        Assert.Equal(1, vm.ConflictCount);
        Assert.Contains("1 个冲突", vm.CountText);
        Assert.Contains("改键", vm.Subtitle);
        Assert.Equal("有冲突", vm.HealthText);
    }

    [Fact]
    public void 无冲突时分组不显示冲突字样()
    {
        var vm = Groups(Source("textpin", "置顶文本", true, Binding("Ctrl+Alt+V", "toggle")))[0];

        Assert.Equal(0, vm.ConflictCount);
        Assert.DoesNotContain("冲突", vm.CountText);
        Assert.Equal("正常", vm.HealthText);
    }

    [Fact]
    public void 插件未运行时冲突仍被标记()
    {
        // 未运行的插件会在下次启动时重新抢键，冲突依然存在，
        // 不该因为"当前没占用"就把高亮藏起来
        var groups = HotkeyGroupBuilder.Build(
        [
            Source("textpin", "置顶文本", false, Binding("Ctrl+Alt+X", "capture", "抓取")),
            Source("screenshot", "截图", true, Binding("Ctrl+Alt+X", "region", "区域截图")),
        ]);

        Assert.True(Row(groups, "textpin", "Ctrl+Alt+X").HasConflict);
    }
}
