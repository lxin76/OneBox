using ClipboardFloat.App.Services;

namespace ClipboardFloat.Tests;

public class AutoStartServiceTests : IDisposable
{
    // 使用独立测试子键，避免污染真实 Run 键
    private const string TestRunKeyPath = @"Software\ClipboardFloatTests\Run";

    public void Dispose()
    {
        AutoStartService.Disable(TestRunKeyPath);
        try
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(
                @"Software\ClipboardFloatTests", throwOnMissingSubKey: false);
        }
        catch
        {
        }
    }

    [Fact]
    public void Initially_NotEnabled()
    {
        Assert.False(AutoStartService.IsEnabled(TestRunKeyPath));
    }

    [Fact]
    public void Enable_WritesExePath()
    {
        AutoStartService.Enable(@"D:\tools\ClipboardFloat.exe", TestRunKeyPath);
        Assert.True(AutoStartService.IsEnabled(TestRunKeyPath));
    }

    [Fact]
    public void Disable_RemovesEntry()
    {
        AutoStartService.Enable(@"D:\tools\ClipboardFloat.exe", TestRunKeyPath);
        AutoStartService.Disable(TestRunKeyPath);
        Assert.False(AutoStartService.IsEnabled(TestRunKeyPath));
    }

    [Fact]
    public void ReEnable_OverwritesOldPath()
    {
        AutoStartService.Enable(@"D:\old\ClipboardFloat.exe", TestRunKeyPath);
        AutoStartService.Enable(@"D:\new\ClipboardFloat.exe", TestRunKeyPath);
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(TestRunKeyPath);
        Assert.Equal("\"D:\\new\\ClipboardFloat.exe\"", key?.GetValue("ClipboardFloat") as string);
    }
}
