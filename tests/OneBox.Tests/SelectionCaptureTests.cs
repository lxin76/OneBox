using OneBox.Plugins.TextPin.Services;

namespace OneBox.Tests;

/// <summary>
/// 抓取选中文字的判定逻辑。真实流程要注入 Ctrl+C 并依赖前台窗口，
/// 因此把"等剪贴板序号变化"这段抽成可注入的 <c>WaitForClipboardText</c>，在这里钉住边界：
/// 序号没变不算成功（避免旧剪贴板内容冒充选中文字）、超时要报错、非文本内容要明确提示。
/// </summary>
public class SelectionCaptureTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(100);

    /// <summary>不做真实等待的 sleep：让超时循环立刻跑完，测试保持毫秒级。</summary>
    private static void NoSleep(int _) { }

    [Fact]
    public void SequenceChanges_ReturnsClipboardText()
    {
        var sequence = 10u;
        var result = SelectionCapture.WaitForClipboardText(
            sequenceBefore: 10,
            readSequence: () => { sequence = 11; return sequence; },
            readText: () => "选中的文字",
            timeout: Timeout,
            sleep: NoSleep);

        Assert.True(result.Success);
        Assert.Equal("选中的文字", result.Text);
        Assert.Null(result.Error);
    }

    [Fact]
    public void SequenceUnchanged_TimesOutWithoutReturningStaleText()
    {
        // 这是最关键的一条：剪贴板序号没变时，绝不能把上一次留在剪贴板里的内容当成选中文字
        var result = SelectionCapture.WaitForClipboardText(
            sequenceBefore: 42,
            readSequence: () => 42,
            readText: () => "上一次复制留下的旧内容",
            timeout: Timeout,
            sleep: NoSleep);

        Assert.False(result.Success);
        Assert.Null(result.Text);
        Assert.Contains("没有取到选中文字", result.Error);
    }

    [Fact]
    public void SequenceChangesButNotText_ReportsNonTextContent()
    {
        // 复制了图片/文件：剪贴板确实变了，但没有文本，要明确提示而不是静默失败
        var result = SelectionCapture.WaitForClipboardText(
            sequenceBefore: 1,
            readSequence: () => 2,
            readText: () => null,
            timeout: Timeout,
            sleep: NoSleep);

        Assert.False(result.Success);
        Assert.Null(result.Text);
        Assert.Contains("不是纯文本", result.Error);
    }

    [Fact]
    public void EmptyText_IsTreatedAsNonText()
    {
        var result = SelectionCapture.WaitForClipboardText(
            sequenceBefore: 1,
            readSequence: () => 2,
            readText: () => "",
            timeout: Timeout,
            sleep: NoSleep);

        Assert.False(result.Success);
        Assert.Contains("不是纯文本", result.Error);
    }

    [Fact]
    public void SlowTarget_IsPickedUpOnLaterPoll()
    {
        // 慢应用可能过几次轮询才更新剪贴板，不能第一次没变就放弃
        var polls = 0;
        var result = SelectionCapture.WaitForClipboardText(
            sequenceBefore: 100,
            readSequence: () => ++polls >= 3 ? 101u : 100u,
            readText: () => "迟到的内容",
            timeout: TimeSpan.FromSeconds(5),
            sleep: NoSleep);

        Assert.True(result.Success);
        Assert.Equal("迟到的内容", result.Text);
        Assert.True(polls >= 3);
    }

    [Fact]
    public void OkAndFail_HelpersCarryExpectedFields()
    {
        var ok = CaptureResult.Ok("x");
        Assert.True(ok.Success);
        Assert.Equal("x", ok.Text);
        Assert.Null(ok.Error);

        var fail = CaptureResult.Fail("原因");
        Assert.False(fail.Success);
        Assert.Null(fail.Text);
        Assert.Equal("原因", fail.Error);
    }

    [Fact]
    public void PollInterval_IsSmallEnoughToFeelResponsive()
    {
        // 轮询间隔决定了"按热键到出结果"的观感延迟，不该大到肉眼可感
        Assert.InRange(SelectionCapture.PollIntervalMs, 5, 100);
    }

    [Fact]
    public void DefaultTimeout_IsLongEnoughForSlowAppsButNotSluggish()
    {
        Assert.InRange(SelectionCapture.DefaultTimeout.TotalMilliseconds, 300, 3000);
    }
}
