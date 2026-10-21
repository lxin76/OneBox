using System.IO;
using OneBox.Plugins.Recorder.Core;

namespace OneBox.Tests;

/// <summary>录制文件名的时间格式与冲突处理。</summary>
public class RecordingFileNameTests
{
    [Fact]
    public void 文件名带秒级时间戳()
    {
        Assert.Equal("录屏_20261021_093623", RecordingFileName.BuildBaseName(new DateTime(2026, 10, 21, 9, 36, 23)));
    }

    [Fact]
    public void 没有冲突时使用基础名()
    {
        var name = RecordingFileName.EnsureUnique(@"C:\rec", "录屏_20261021_093623", _ => false);

        Assert.Equal("录屏_20261021_093623.mp4", name);
    }

    [Fact]
    public void 冲突时依次追加序号()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "录屏_20261021_093623.mp4",
            "录屏_20261021_093623_2.mp4",
        };

        var name = RecordingFileName.EnsureUnique(@"C:\rec", "录屏_20261021_093623",
            path => taken.Contains(Path.GetFileName(path)));

        Assert.Equal("录屏_20261021_093623_3.mp4", name);
    }

    [Fact]
    public void 扩展名固定为mp4()
    {
        Assert.Equal(".mp4", RecordingFileName.Extension);
    }
}
