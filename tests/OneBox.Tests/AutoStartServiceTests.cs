using OneBox.Plugins.Clipboard.Services;

namespace OneBox.Tests;

/// <summary>
/// 插件侧只读的自启动查询：注册/注销由宿主负责，插件只读取宿主写入的 Run 项。
/// </summary>
public class AutoStartServiceTests : IDisposable
{
    // 使用独立测试子键，避免污染真实 Run 键
    private const string TestRunKeyPath = @"Software\OneBoxTests\Run";
    private const string TestValueName = "OneBoxTest";

    public void Dispose()
    {
        try
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(
                @"Software\OneBoxTests", throwOnMissingSubKey: false);
        }
        catch
        {
        }
    }

    [Fact]
    public void MissingValue_ReturnsFalse()
    {
        Assert.False(AutoStartService.IsEnabled(TestValueName, TestRunKeyPath));
    }

    [Fact]
    public void ExistingValue_ReturnsTrue()
    {
        using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(TestRunKeyPath))
            key.SetValue(TestValueName, "\"D:\\tools\\OneBox.exe\"");

        Assert.True(AutoStartService.IsEnabled(TestValueName, TestRunKeyPath));
    }

    [Fact]
    public void EmptyValue_ReturnsFalse()
    {
        using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(TestRunKeyPath))
            key.SetValue(TestValueName, "");

        Assert.False(AutoStartService.IsEnabled(TestValueName, TestRunKeyPath));
    }
}
