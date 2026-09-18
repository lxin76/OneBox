using ClipboardFloat.Core;

namespace ClipboardFloat.Tests;

public class JsonPropertyListTests
{
    /// <summary>用户给的样例：13 个扁平属性。</summary>
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

    [Fact]
    public void 样例JSON_展开出全部属性()
    {
        var set = JsonPropertyList.TryBuild(Sample);

        Assert.NotNull(set);
        Assert.Equal(13, set!.Items.Count);
        Assert.False(set.Truncated);
        Assert.Equal(new[]
        {
            "menuId", "applicationId", "menuCode", "menuName", "icon", "parentId",
            "menuLevel", "menuIndex", "pageUrl", "identifier", "menuGroup", "isVisible", "id",
        }, set.Items.Select(i => i.Path).ToArray());
    }

    [Fact]
    public void 值_字符串不带引号_数字与布尔保持原样()
    {
        var items = JsonPropertyList.TryBuild(Sample)!.Items;

        Assert.Equal("TMS::BaseInfo:Line", items.Single(i => i.Path == "menuCode").DisplayValue);
        Assert.Equal("\"TMS::BaseInfo:Line\"", items.Single(i => i.Path == "menuCode").RawValue);
        Assert.Equal("20110", items.Single(i => i.Path == "menuId").DisplayValue);
        Assert.Equal("true", items.Single(i => i.Path == "isVisible").DisplayValue);
        Assert.Equal("", items.Single(i => i.Path == "icon").DisplayValue);
        Assert.Equal("线路管理", items.Single(i => i.Path == "menuName").DisplayValue);
    }

    [Fact]
    public void 属性对片段_可直接粘贴回JSON()
    {
        var items = JsonPropertyList.TryBuild(Sample)!.Items;
        Assert.Equal("\"menuCode\": \"TMS::BaseInfo:Line\"", items.Single(i => i.Path == "menuCode").PairText);
        Assert.Equal("\"menuId\": 20110", items.Single(i => i.Path == "menuId").PairText);
    }

    [Fact]
    public void 嵌套对象与数组_路径带下标()
    {
        const string text = """
        { "a": { "b": [1, { "c": "d" }] }, "e": [] }
        """;
        var set = JsonPropertyList.TryBuild(text);

        Assert.NotNull(set);
        Assert.Equal(new[] { "a.b[0]", "a.b[1].c" }, set!.Items.Select(i => i.Path).ToArray());
        Assert.Equal("[0]", set.Items[0].Key);
        Assert.Equal("d", set.Items[1].DisplayValue);
    }

    [Fact]
    public void 数组元素与下划线_路径拼接正确()
    {
        const string text = """{ "list": [ { "x": 1 }, { "x": 2 } ] }""";
        var paths = JsonPropertyList.TryBuild(text)!.Items.Select(i => i.Path).ToArray();
        Assert.Equal(new[] { "list[0].x", "list[1].x" }, paths);
    }

    [Fact]
    public void 非JSON_返回null()
    {
        Assert.Null(JsonPropertyList.TryBuild("hello world"));
        Assert.Null(JsonPropertyList.TryBuild("{ not json }"));
        Assert.Null(JsonPropertyList.TryBuild(""));
        Assert.Null(JsonPropertyList.TryBuild(null));
        Assert.Null(JsonPropertyList.TryBuild("123"));            // 标量根不算属性表
        Assert.Null(JsonPropertyList.TryBuild("\"abc\""));
    }

    [Fact]
    public void 合法JSON判定()
    {
        Assert.True(JsonPropertyList.IsJson(Sample));
        Assert.True(JsonPropertyList.IsJson("[1, 2, 3]"));
        Assert.False(JsonPropertyList.IsJson("menuCode: TMS"));
    }

    [Fact]
    public void 超出上限_截断并标记()
    {
        const string text = """{ "a": 1, "b": 2, "c": 3 }""";
        var set = JsonPropertyList.TryBuild(text, maxItems: 2);

        Assert.NotNull(set);
        Assert.Equal(2, set!.Items.Count);
        Assert.True(set.Truncated);
    }

    [Fact]
    public void 允许结尾逗号与注释()
    {
        const string text = """
        {
          "a": 1, // 注释
          "b": 2,
        }
        """;
        var set = JsonPropertyList.TryBuild(text);
        Assert.NotNull(set);
        Assert.Equal(2, set!.Items.Count);
    }
}
