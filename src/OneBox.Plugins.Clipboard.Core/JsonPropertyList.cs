using System.Text.Json;

namespace OneBox.Plugins.Clipboard.Core;

/// <summary>JSON 中的一个可复制叶子属性。</summary>
/// <param name="Path">属性路径，如 <c>menuCode</c> 或 <c>a.b[0].c</c>（数组下标用 <c>[i]</c>）。</param>
/// <param name="Key">属性名（数组元素为 <c>[i]</c>）。</param>
/// <param name="RawValue">原始 JSON 字面量（字符串含引号）。</param>
/// <param name="DisplayValue">用于显示/复制的值：字符串不带引号，其余与原文一致。</param>
/// <param name="PairText">完整属性对片段，如 <c>"menuCode": "TMS::BaseInfo:Line"</c>。</param>
public sealed record JsonPropertyInfo(
    string Path, string Key, string RawValue, string DisplayValue, string PairText)
{
    /// <summary>供界面/无障碍朗读的可读文本。</summary>
    public override string ToString() => $"{Path} = {DisplayValue}";
}

/// <summary>JSON 属性展开结果；<paramref name="Truncated"/> 表示因条目上限被截断。</summary>
public sealed record JsonPropertySet(IReadOnlyList<JsonPropertyInfo> Items, bool Truncated);

/// <summary>把 JSON 文本展开成「属性路径 = 值」列表，供查看窗口的属性面板使用。</summary>
public static class JsonPropertyList
{
    public const int DefaultMaxItems = 2000;

    /// <summary>尝试解析并展开叶子属性；不是对象/数组形式的合法 JSON 时返回 null。</summary>
    public static JsonPropertySet? TryBuild(string? text, int maxItems = DefaultMaxItems)
    {
        if (string.IsNullOrWhiteSpace(text) || maxItems <= 0) return null;

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
        }
        catch (JsonException)
        {
            return null;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array)) return null;

            var items = new List<JsonPropertyInfo>();
            bool truncated = Walk(root, parentPath: "", key: "", items, maxItems);
            return new JsonPropertySet(items, truncated);
        }
    }

    /// <summary>文本是否为对象/数组形式的合法 JSON。</summary>
    public static bool IsJson(string? text) => TryBuild(text, maxItems: 1) is not null;

    private static bool Walk(JsonElement element, string parentPath, string key,
        List<JsonPropertyInfo> items, int maxItems)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var path = parentPath.Length == 0 ? property.Name : $"{parentPath}.{property.Name}";
                    if (Walk(property.Value, path, property.Name, items, maxItems)) return true;
                }
                return false;

            case JsonValueKind.Array:
                int index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    var path = $"{parentPath}[{index}]";
                    if (Walk(item, path, $"[{index}]", items, maxItems)) return true;
                    index++;
                }
                return false;

            default:
                items.Add(Create(parentPath, key, element));
                return items.Count >= maxItems;
        }
    }

    private static JsonPropertyInfo Create(string path, string key, JsonElement element)
    {
        var raw = element.GetRawText();
        var display = element.ValueKind == JsonValueKind.String ? element.GetString() ?? "" : raw;
        var pair = key.Length == 0 ? raw : $"\"{Escape(key)}\": {raw}";
        return new JsonPropertyInfo(path, key, raw, display, pair);
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
