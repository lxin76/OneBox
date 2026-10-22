using System.IO;
using OneBox.Abstractions.Logging;
using OneBox.Plugin.Sdk.Logging;

namespace OneBox.Tests;

/// <summary>
/// 单文件日志：永远只有一个文件、超过上限时丢弃最旧内容、保留区从完整行开始。
///
/// 这些断言直接对应需求：日志不能按日期/大小轮转出多个文件，写满 10 MB 后要覆盖最旧内容。
/// </summary>
public class LogFileTests : IDisposable
{
    private readonly string _root;

    public LogFileTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"onebox-log-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Write_AppendsTimestampAndTag()
    {
        var path = Path.Combine(_root, "host.log");
        var log = new LogFile(path);

        log.Write("HOST", "hello");

        var text = File.ReadAllText(path);
        Assert.Matches(@"^\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}\] \[HOST\] hello", text);
    }

    [Fact]
    public void Write_KeepsSingleFileUnderLimit_AndDropsOldest()
    {
        var path = Path.Combine(_root, "single.log");
        var log = new LogFile(path, maxBytes: 4096);

        for (var i = 0; i < 200; i++)
            log.Write("TEST", $"line-{i:D3} {new string('x', 80)}");

        Assert.True(new FileInfo(path).Length <= 4096);
        var text = File.ReadAllText(path);
        Assert.Contains("line-199", text);
        Assert.DoesNotContain("line-000", text);
        Assert.False(File.Exists(path + ".1"));
        Assert.Single(Directory.GetFiles(_root));
    }

    [Fact]
    public void Trim_StartsAtLineBoundary()
    {
        var path = Path.Combine(_root, "boundary.log");
        var log = new LogFile(path, maxBytes: 4096);

        for (var i = 0; i < 300; i++)
            log.Write("TEST", $"line-{i:D3} {new string('y', 60)}");

        var text = File.ReadAllText(path);
        foreach (var line in text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
            Assert.StartsWith("[", line, StringComparison.Ordinal);
    }

    [Fact]
    public void PluginLog_WritesToSinglePluginFile()
    {
        var directory = Path.Combine(_root, "logs");
        PluginLog.Initialize(directory, "textpin");
        PluginLog.Write("hello");

        var path = Path.Combine(directory, "plugin-textpin.log");
        Assert.True(File.Exists(path));
        Assert.Contains("[textpin] hello", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(directory));
    }

    [Fact]
    public void OversizedSingleLine_IsTruncatedWithinLimit()
    {
        var path = Path.Combine(_root, "huge.log");
        var log = new LogFile(path, maxBytes: 2048);

        log.Write("TEST", new string('z', 10_000));
        log.Write("TEST", "after");

        Assert.True(new FileInfo(path).Length <= 2048);
        Assert.Contains("after", File.ReadAllText(path));
    }

    [Fact]
    public void Write_WhenOldContentPlusNewLineExceedsLimit_StillStaysUnderLimit()
    {
        // 回归：文件恰好到上限时，裁剪必须为下一条留出空间，否则会写成 上限 + 新行。
        var path = Path.Combine(_root, "near.log");
        var log = new LogFile(path, maxBytes: 2048);

        log.Write("TEST", new string('a', 1900));
        log.Write("TEST", new string('b', 300));

        Assert.True(new FileInfo(path).Length <= 2048);
    }

    [Fact]
    public void ConcurrentWrites_NeverExceedLimit()
    {
        var path = Path.Combine(_root, "concurrent.log");
        var log = new LogFile(path, maxBytes: 8192);

        Parallel.For(0, 8, worker =>
        {
            for (var i = 0; i < 100; i++)
                log.Write("TEST", $"worker-{worker} line-{i} {new string('c', 120)}");
        });

        Assert.True(new FileInfo(path).Length <= 8192);
    }
}
