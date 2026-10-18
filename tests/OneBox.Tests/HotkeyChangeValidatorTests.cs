using OneBox.Host.Services;

namespace OneBox.Tests;

/// <summary>改键时的冲突检查：这是"自定义快捷键"最需要在保存前挡住的部分。</summary>
public class HotkeyChangeValidatorTests
{
    private static HotkeyOccupant Occupant(
        string pluginId, string actionId, string combo, string? name = null, bool isFixed = false)
        => new()
        {
            PluginId = pluginId,
            PluginName = name ?? pluginId,
            ActionId = actionId,
            Description = actionId,
            Combination = combo,
            Fixed = isFixed,
        };

    [Fact]
    public void NoConflict_AllowsApply()
    {
        var occupants = new[]
        {
            Occupant("textpin", "toggle", "Ctrl+Alt+V"),
            Occupant("screenshot", "region", "Ctrl+Alt+A"),
        };

        var check = HotkeyChangeValidator.Check("Ctrl+Alt+Q", "textpin", "toggle", occupants);

        Assert.True(check.CanApply);
        Assert.Null(check.Error);
        Assert.Empty(check.Conflicts);
    }

    [Fact]
    public void DuplicateWithOtherPlugin_BlocksAndNamesPeer()
    {
        var occupants = new[]
        {
            Occupant("textpin", "toggle", "Ctrl+Alt+V"),
            Occupant("screenshot", "region", "Ctrl+Alt+A", "截图"),
        };

        var check = HotkeyChangeValidator.Check("Ctrl+Alt+A", "textpin", "toggle", occupants);

        Assert.False(check.CanApply);
        Assert.Contains("截", check.Error);          // 点名了冲突方
        Assert.Single(check.Conflicts);
        Assert.Contains("截图", check.Conflicts[0].Describe());
    }

    [Fact]
    public void DuplicateWithSamePlugin_Blocks()
    {
        var occupants = new[]
        {
            Occupant("textpin", "toggle", "Ctrl+Alt+V"),
            Occupant("textpin", "paste", "Ctrl+Alt+P"),
        };

        var check = HotkeyChangeValidator.Check("Ctrl+Alt+P", "textpin", "toggle", occupants);

        Assert.False(check.CanApply);
        Assert.Single(check.Conflicts);
    }

    [Fact]
    public void SameBindingIsNotSelfConflict()
    {
        // 本动作自己原来就占着这个键，再设一遍不该视为冲突
        var occupants = new[] { Occupant("textpin", "toggle", "Ctrl+Alt+V") };

        var check = HotkeyChangeValidator.Check("Ctrl+Alt+V", "textpin", "toggle", occupants);

        Assert.True(check.CanApply);
    }

    [Fact]
    public void OwnOldKeyIsReleasedForComparison()
    {
        // 把 toggle 从一个键改到另一个键时，它原来的键不应再被算作"占用"，
        // 否则会出现"改成任何键都和自己旧键冲突"的荒唐结果
        var occupants = new[]
        {
            Occupant("textpin", "toggle", "Ctrl+Alt+V"),
            Occupant("textpin", "paste", "Ctrl+Alt+P"),
        };

        // 把 paste 改成 toggle 已占用的键 → 冲突，必须挡住
        Assert.False(HotkeyChangeValidator.Check("Ctrl+Alt+V", "textpin", "paste", occupants).CanApply);

        // 把 toggle 改成一个没人用的键 → 通过（它自己的旧键不构成阻碍）
        Assert.True(HotkeyChangeValidator.Check("Ctrl+Alt+Q", "textpin", "toggle", occupants).CanApply);
    }

    [Theory]
    [InlineData("Ctrl+Alt+V", "alt+ctrl+v")]
    [InlineData("Ctrl+Alt+V", "Alt+Ctrl+V")]
    [InlineData("Ctrl+Alt+V", "CTRL+ALT+V")]
    public void ConflictIsSpellingInsensitive(string existing, string candidate)
    {
        var occupants = new[] { Occupant("other", "act", existing) };

        Assert.False(HotkeyChangeValidator.Check(candidate, "textpin", "toggle", occupants).CanApply);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void EmptyCombination_Blocks(string? combo)
    {
        var check = HotkeyChangeValidator.Check(combo, "p", "a", Array.Empty<HotkeyOccupant>());

        Assert.False(check.CanApply);
        Assert.Contains("组合键", check.Error);
    }

    [Theory]
    [InlineData("V")]        // 无修饰键
    [InlineData("F5")]       // 无修饰键
    [InlineData("Ctrl+")]    // 只有修饰键
    [InlineData("NotAKey")]  // 无法识别
    public void InvalidCombination_Blocks(string combo)
    {
        var check = HotkeyChangeValidator.Check(combo, "p", "a", Array.Empty<HotkeyOccupant>());

        Assert.False(check.CanApply);
        Assert.NotNull(check.Error);
    }

    [Fact]
    public void WinOnlyCombination_Blocks()
    {
        var check = HotkeyChangeValidator.Check("Win+V", "p", "a", Array.Empty<HotkeyOccupant>());

        Assert.False(check.CanApply);
        Assert.Contains("Win", check.Error);
    }

    [Fact]
    public void WinWithOtherModifier_IsAllowed()
    {
        var check = HotkeyChangeValidator.Check("Win+Shift+V", "p", "a", Array.Empty<HotkeyOccupant>());

        Assert.True(check.CanApply);
    }

    [Fact]
    public void FixedHotkey_StillOccupiesItsKey()
    {
        // 固定热键不可改，但它的键同样不能被别人占用
        var occupants = new[] { Occupant("textpin", "resetKeys", "Ctrl+Alt+K", isFixed: true) };

        var check = HotkeyChangeValidator.Check("Ctrl+Alt+K", "screenshot", "region", occupants);

        Assert.False(check.CanApply);
    }

    [Fact]
    public void StoppedPluginHotkey_StillCountsAsOccupied()
    {
        // 未运行插件的热键照样参与仲裁，否则"停着时改键、一启动就撞"
        var occupants = new[] { Occupant("stopped", "act", "Ctrl+Alt+Z") };

        Assert.False(HotkeyChangeValidator.Check("Ctrl+Alt+Z", "textpin", "toggle", occupants).CanApply);
    }

    [Fact]
    public void MultipleConflicts_AreAllListed()
    {
        var occupants = new[]
        {
            Occupant("a", "x", "Ctrl+Alt+Q", "甲"),
            Occupant("b", "y", "Ctrl+Alt+Q", "乙"),
        };

        var check = HotkeyChangeValidator.Check("Ctrl+Alt+Q", "c", "z", occupants);

        Assert.False(check.CanApply);
        Assert.Equal(2, check.Conflicts.Count);
    }

    [Fact]
    public void InvalidOccupantCombination_IsIgnored()
    {
        var occupants = new[] { Occupant("broken", "act", "这不是组合键") };

        Assert.True(HotkeyChangeValidator.Check("Ctrl+Alt+Q", "p", "a", occupants).CanApply);
    }

    // ---- 外部占用探测 ----

    [Fact]
    public void OccupiedByOtherApp_IsFlaggedButStillApplies()
    {
        // 核心约定：与其他软件冲突只强提示、不阻止保存——
        // 两个软件错开运行就不会撞车，用户应当能保存
        var check = HotkeyChangeValidator.Check("Ctrl+Alt+Q", "p", "a", Array.Empty<HotkeyOccupant>());
        var merged = HotkeyChangeValidator.WithAvailabilityProbe(check, available: false, "Ctrl+Alt+Q");

        Assert.True(merged.CanApply);
        Assert.True(merged.HasExternalConflict);
        Assert.True(merged.OccupiedByOtherApp);
        Assert.Null(merged.Error);
        Assert.NotNull(merged.ExternalConflictMessage);
        Assert.Contains("其他软件", merged.ExternalConflictMessage);
        Assert.Contains("Ctrl+Alt+Q", merged.ExternalConflictMessage);
    }

    [Fact]
    public void ExternalConflictMessage_MentionsThatSavingIsAllowed()
    {
        var check = HotkeyChangeValidator.Check("Ctrl+Alt+Q", "p", "a", Array.Empty<HotkeyOccupant>());
        var merged = HotkeyChangeValidator.WithAvailabilityProbe(check, available: false, "Ctrl+Alt+Q");

        // 提示要讲清楚"为什么可以照样保存"，否则用户会以为被拦下了
        Assert.Contains("不影响保存", merged.ExternalConflictMessage);
    }

    [Fact]
    public void AvailableProbe_LeavesNoExternalConflict()
    {
        var check = HotkeyChangeValidator.Check("Ctrl+Alt+Q", "p", "a", Array.Empty<HotkeyOccupant>());
        var merged = HotkeyChangeValidator.WithAvailabilityProbe(check, available: true, "Ctrl+Alt+Q");

        Assert.False(merged.HasExternalConflict);
        Assert.Null(merged.ExternalConflictMessage);
    }

    [Fact]
    public void InternalConflict_IsNotReportedAsExternal()
    {
        // 两类冲突必须能区分：插件间撞车要改键，被别的软件占用未必
        var occupants = new[] { Occupant("other", "act", "Ctrl+Alt+A") };
        var check = HotkeyChangeValidator.Check("Ctrl+Alt+A", "textpin", "toggle", occupants);
        var merged = HotkeyChangeValidator.WithAvailabilityProbe(check, available: false, "Ctrl+Alt+A");

        Assert.False(merged.CanApply);
        Assert.False(merged.HasExternalConflict);
        Assert.Null(merged.ExternalConflictMessage);
    }

    [Fact]
    public void ProbeDoesNotOverrideHardError()
    {
        var failed = new HotkeyChangeCheck { Error = "已冲突" };

        var merged = HotkeyChangeValidator.WithAvailabilityProbe(failed, available: false, "Ctrl+Alt+Q");

        Assert.Equal("已冲突", merged.Error);
        Assert.False(merged.HasExternalConflict); // 已有硬性错误时不再叠加外部提示，免得提示混乱
    }

    [Fact]
    public void NullProbeResult_LeavesCheckUntouched()
    {
        var check = HotkeyChangeValidator.Check("Ctrl+Alt+Q", "p", "a", Array.Empty<HotkeyOccupant>());

        // null 表示"没能探测"（例如环境不支持），不应被当成冲突
        var merged = HotkeyChangeValidator.WithAvailabilityProbe(check, available: null, "Ctrl+Alt+Q");

        Assert.True(merged.CanApply);
        Assert.False(merged.HasExternalConflict);
    }
}
