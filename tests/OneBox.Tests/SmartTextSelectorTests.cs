using OneBox.Plugins.Clipboard.Core;

namespace OneBox.Tests;

public class SmartTextSelectorTests
{
    /// <summary>用户给的样例：扁平 JSON 对象（数字 / 字符串 / 空串 / 布尔）。</summary>
    private const string Sample = """
{
    "menuId": 20110,
    "applicationId": 10001,
    "menuCode": "TMS::BaseInfo:Line",
    "menuName": "线路管理",
    "icon": "",
    "parentId": 10013,
    "menuLevel": 2,
    "menuIndex": 1,
    "pageUrl": "/baseinfo/line",
    "identifier": "Lines",
    "menuGroup": "基础信息",
    "isVisible": true,
    "id": 20110
  }
""";

    private static string? TextAt(string text, int index, int level)
    {
        var selection = SmartTextSelector.Select(text, index, level);
        return selection is null ? null : text.Substring(selection.Range.Start, selection.Range.Length);
    }

    private static string? LabelAt(string text, int index, int level)
        => SmartTextSelector.Select(text, index, level)?.Label;

    [Fact]
    public void 双击字符串值_选中引号内全部内容()
    {
        var index = Sample.IndexOf("TMS::", StringComparison.Ordinal) + 2;
        Assert.Equal("TMS::BaseInfo:Line", TextAt(Sample, index, 0));
        Assert.Equal("字符串值", LabelAt(Sample, index, 0));
    }

    [Fact]
    public void Ctrl双击_选中整条属性对()
    {
        var index = Sample.IndexOf("TMS::", StringComparison.Ordinal) + 2;
        Assert.Equal("\"menuCode\": \"TMS::BaseInfo:Line\"", TextAt(Sample, index, 1));
        Assert.Equal("属性对", LabelAt(Sample, index, 1));
    }

    [Fact]
    public void 双击数值_选中数字本身()
    {
        var index = Sample.IndexOf("20110", StringComparison.Ordinal);
        Assert.Equal("20110", TextAt(Sample, index, 0));
        Assert.Equal("\"menuId\": 20110", TextAt(Sample, index, 1));
    }

    [Fact]
    public void 双击布尔_选中true()
    {
        var index = Sample.IndexOf("true", StringComparison.Ordinal);
        Assert.Equal("true", TextAt(Sample, index, 0));
        Assert.Equal("\"isVisible\": true", TextAt(Sample, index, 1));
    }

    [Fact]
    public void 空字符串值_退化为整条属性对()
    {
        var marker = "\"icon\": \"\"";
        var index = Sample.IndexOf(marker, StringComparison.Ordinal) + marker.Length - 1;
        Assert.Equal(marker, TextAt(Sample, index, 0));
    }

    [Fact]
    public void 点在键上_也是属性对()
    {
        var index = Sample.IndexOf("\"pageUrl\"", StringComparison.Ordinal) + 4;
        Assert.Equal("pageUrl", TextAt(Sample, index, 0));            // 双击键 → 键本身
        Assert.Equal("\"pageUrl\": \"/baseinfo/line\"", TextAt(Sample, index, 1));
    }

    [Fact]
    public void 继续扩大_到整行_再到整个对象()
    {
        var index = Sample.IndexOf("线路管理", StringComparison.Ordinal);
        Assert.Equal("线路管理", TextAt(Sample, index, 0));
        Assert.Equal("\"menuName\": \"线路管理\"", TextAt(Sample, index, 1));
        Assert.Equal("\"menuName\": \"线路管理\",", TextAt(Sample, index, 2));   // 整行（含逗号、不含缩进）
        var all = TextAt(Sample, index, 3)!;
        Assert.Contains("\"menuId\": 20110", all);
        Assert.DoesNotContain("{", all);                                        // 区块内容：不含外层大括号
        Assert.Equal(Sample.Trim(), TextAt(Sample, index, 99));                 // 层级超大时取最大
    }

    [Fact]
    public void 普通文本_双击选中整行内容()
    {
        const string text = "第一行内容\n  第二行内容  \n第三行";
        var index = text.IndexOf("第二行", StringComparison.Ordinal);
        Assert.Equal("第二行内容", TextAt(text, index, 0));
    }

    [Fact]
    public void 单行嵌套JSON_逐层扩大且只增不减()
    {
        const string text = "{\"a\": {\"b\": 1}}";
        var index = text.IndexOf('1');
        Assert.Equal("1", TextAt(text, index, 0));
        Assert.Equal("\"b\": 1", TextAt(text, index, 1));
        Assert.Equal(text, TextAt(text, index, 2));
        Assert.Equal(text, TextAt(text, index, 99));
    }

    [Fact]
    public void 字符串里的冒号不会被误判为属性对()
    {
        const string text = "{\"url\": \"http://a:b\"}";
        var index = text.IndexOf("a:b", StringComparison.Ordinal);
        Assert.Equal("http://a:b", TextAt(text, index, 0));
        Assert.Equal("\"url\": \"http://a:b\"", TextAt(text, index, 1));
    }

    [Fact]
    public void 字符串里的括号不参与区块配对()
    {
        const string text = "{\"a\": \"x{y}z\", \"b\": 2}";
        var index = text.IndexOf('y');
        Assert.Equal("x{y}z", TextAt(text, index, 0));

        // 任何一层都不应该出现把字符串内容当区块的 “{y}”
        var candidates = SmartTextSelector.BuildCandidates(text, index);
        Assert.DoesNotContain(candidates,
            c => text.Substring(c.Range.Start, c.Range.Length) == "{y}");
        Assert.Equal(text, TextAt(text, index, 99));
    }

    [Fact]
    public void 数组元素_按元素层级扩大()
    {
        const string text = "[\"one\", \"two\", \"three\"]";
        var index = text.IndexOf("two", StringComparison.Ordinal);
        Assert.Equal("two", TextAt(text, index, 0));
        Assert.Equal(text, TextAt(text, index, 99));
    }

    [Fact]
    public void 空文本_返回null()
    {
        Assert.Null(SmartTextSelector.Select("", 0, 0));
        Assert.Null(SmartTextSelector.Select(null, 0, 0));
    }

    [Fact]
    public void 下标越界_自动收敛到末尾()
    {
        var selection = SmartTextSelector.Select(Sample, 99999, 0);
        Assert.NotNull(selection);
        Assert.True(selection!.Range.End <= Sample.Length);
    }
}
