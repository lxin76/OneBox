using System.IO;
using OneBox.Plugins.Screenshot.Core;

namespace OneBox.Tests;

public class FileNameBuilderTests
{
    private static readonly DateTime Sample = new(2026, 3, 14, 15, 9, 26, 535);

    [Fact]
    public void 替换时间占位符()
    {
        Assert.Equal("Shot_20260314_150926", FileNameBuilder.Build("Shot_{yyyyMMdd_HHmmss}", Sample));
    }

    [Fact]
    public void 支持年月日分目录式占位符()
    {
        Assert.Equal("2026-03-14_1509", FileNameBuilder.Build("{yyyy}-{MM}-{dd}_{HH}{mm}", Sample));
    }

    [Fact]
    public void 时间格式里的冒号被替换为短横线()
    {
        // ":" 是 Windows 文件名非法字符，必须替换掉才能落盘
        Assert.Equal("15-09-26", FileNameBuilder.Build("{HH:mm:ss}", Sample));
    }

    [Fact]
    public void 模板为空时回退默认名()
    {
        Assert.Equal("Shot_20260314_150926", FileNameBuilder.Build(null, Sample));
        Assert.Equal("Shot_20260314_150926", FileNameBuilder.Build("   ", Sample));
    }

    [Fact]
    public void 非法字符被清理()
    {
        Assert.Equal("a-b-c", FileNameBuilder.Build("a:b*c", Sample));
    }

    [Fact]
    public void 非时间花括号按原文保留()
    {
        Assert.Equal("图{截图}", FileNameBuilder.Build("图{截图}", Sample));
    }

    [Fact]
    public void 非法字符被替换而非丢弃()
    {
        // 非法字符统一改写成 "-"，因此 ":::" 会变成合法但无意义的 "---"
        Assert.Equal("---", FileNameBuilder.Build(":::", Sample));
    }

    [Fact]
    public void 清理后为空时回退默认名()
    {
        // 控制字符会被清掉，模板只剩控制字符时结果为空，回退到 "Shot"
        Assert.Equal("Shot", FileNameBuilder.Build("\u0001\u0002", Sample));
    }

    [Fact]
    public void 纯空白模板回退到默认模板()
    {
        Assert.Equal("Shot_20260314_150926", FileNameBuilder.Build("\t  \n", Sample));
    }

    [Fact]
    public void 扩展名按格式决定()
    {
        Assert.Equal(".png", FileNameBuilder.ExtensionFor(ShotImageFormat.Png));
        Assert.Equal(".jpg", FileNameBuilder.ExtensionFor(ShotImageFormat.Jpeg));
    }

    [Fact]
    public void 重名时自动追加序号()
    {
        var dir = Path.Combine(Path.GetTempPath(), "OneBoxTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // 已占用前两个候选名 → 应生成第 3 个
            var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                Path.Combine(dir, "Shot.png"),
                Path.Combine(dir, "Shot (2).png"),
            };

            var path = FileNameBuilder.BuildUniquePath(dir, "Shot", ".png", reserved);
            Assert.Equal(Path.Combine(dir, "Shot (3).png"), path);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void 扩展名缺少点时自动补上()
    {
        var dir = Path.Combine(Path.GetTempPath(), "OneBoxTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = FileNameBuilder.BuildUniquePath(dir, "Shot", "png");
            Assert.EndsWith(".png", path);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
