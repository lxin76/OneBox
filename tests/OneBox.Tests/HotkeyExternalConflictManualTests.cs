using OneBox.Host.Services;

namespace OneBox.Tests;

/// <summary>
/// 外部冲突判定的边界（不依赖真实占用）：
/// 只验证"探测到被占用时，结论是不阻断保存、且提示足够醒目"这条业务约定。
/// 真实占用由 HotkeyProbeTests 与手工验证覆盖。
/// </summary>
public class HotkeyExternalConflictManualTests
{
    [Fact]
    public void OccupiedByOtherApp_CanStillBeSaved()
    {
        // 用户明确要求：与其他软件冲突时强提示，但**不阻止**保存
        var check = HotkeyChangeValidator.Check("Ctrl+Alt+Y", "p", "a", Array.Empty<HotkeyOccupant>());
        var merged = HotkeyChangeValidator.WithAvailabilityProbe(check, available: false, "Ctrl+Alt+Y");

        Assert.True(merged.CanApply);
        Assert.True(merged.HasExternalConflict);
        Assert.Contains("其他软件", merged.ExternalConflictMessage);
    }

    [Fact]
    public void ProbeBasedUnavailable_MatchesRealProbeContract()
    {
        // 真实探测返回 bool?，false 才代表被占用
        Assert.True(HotkeyService.ProbeAvailable("Ctrl+Alt+Shift+F20"));
    }
}
