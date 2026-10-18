using System.Text;
using OneBox.Host.Services;

namespace OneBox.Tests;

/// <summary>
/// 命令行开关，以及"右键菜单点一下"这条链路。
///
/// 右键菜单项只能执行命令行：点一下会启动新的 OneBox.exe，它再把手上的请求转交给正在运行的实例。
/// 这条链路跨了两个进程，任何一段读错字符串，结果都是"点了没反应"，而且从注册表里看不出问题——
/// 所以命令行的生成、解析、以及转交请求的编解码都在这里钉死。
/// </summary>
public class HostRequestTests
{
    private const string ExePath = @"C:\Tools\OneBox\OneBox.exe";

    [Theory]
    [InlineData("--toolbox")]
    [InlineData("--TOOLBOX")]
    [InlineData("/toolbox")]
    public void 读取开关时忽略大小写与前缀(string argument)
        => Assert.Equal("toolbox", HostRequest.ParseCommandLine([argument]));

    [Fact]
    public void 没有请求时返回空()
    {
        Assert.Null(HostRequest.ParseCommandLine([]));
        Assert.Null(HostRequest.ParseCommandLine([ExePath]));
        Assert.Null(HostRequest.ParseCommandLine([ExePath, "--menu-invoke"]));
        Assert.Null(HostRequest.ParseCommandLine([ExePath, "--menu-invoke", "quicknote"]));
    }

    [Fact]
    public void 工具箱的命令行能被自己读回来()
    {
        var command = HostRequest.BuildSwitchCommandLine(ExePath, HostRequest.Toolbox);

        Assert.Equal($@"""{ExePath}"" --toolbox", command);
        Assert.Equal("toolbox", HostRequest.ParseCommandLine(Tokenize(command)));
    }

    [Fact]
    public void 小工具菜单项的命令行能被自己读回来()
    {
        var command = HostRequest.BuildMenuInvokeCommandLine(ExePath, "quicknote", "show");
        var request = HostRequest.ParseCommandLine(Tokenize(command));

        Assert.NotNull(request);
        Assert.True(HostRequest.TryUnpackMenuInvoke(request, out var pluginId, out var itemId));
        Assert.Equal("quicknote", pluginId);
        Assert.Equal("show", itemId);
    }

    [Fact]
    public void 含空格与中文的标识也能原样往返()
    {
        var command = HostRequest.BuildMenuInvokeCommandLine(ExePath, "textpin", "抓取 选中 文字");
        var request = HostRequest.ParseCommandLine(Tokenize(command));

        Assert.True(HostRequest.TryUnpackMenuInvoke(request, out var pluginId, out var itemId));
        Assert.Equal("textpin", pluginId);
        Assert.Equal("抓取 选中 文字", itemId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("toolbox")]
    [InlineData("menu-invoke:quicknote")]
    [InlineData("menu-invoke:quicknote:")]
    [InlineData("menu-invoke::show")]
    public void 不是菜单请求或残缺的请求一律拒绝(string? request)
        => Assert.False(HostRequest.TryUnpackMenuInvoke(request, out _, out _));

    [Fact]
    public void 转交请求能原样解开()
    {
        var request = HostRequest.PackMenuInvoke("screenshot", "区域 截图");

        Assert.True(HostRequest.TryUnpackMenuInvoke(request, out var pluginId, out var itemId));
        Assert.Equal("screenshot", pluginId);
        Assert.Equal("区域 截图", itemId);
    }

    /// <summary>把命令行切成参数，模拟 Windows 的引号规则（够这里用）。</summary>
    private static string[] Tokenize(string commandLine)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        foreach (var c in commandLine)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (c == ' ' && !inQuotes)
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0) tokens.Add(current.ToString());
        return [.. tokens];
    }
}
