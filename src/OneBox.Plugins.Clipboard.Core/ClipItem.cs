namespace OneBox.Plugins.Clipboard.Core;

public enum ClipKind
{
    Text,
    Html,
    Rtf,
    Image,
    Files,
}

/// <summary>一条剪贴板历史记录（同时保存该次复制携带的所有可用格式）。</summary>
public sealed record ClipItem(
    ClipKind Kind,
    string? Text,
    string? Html,
    string? Rtf,
    byte[]? ImagePng,
    IReadOnlyList<string>? Files,
    string Preview,
    DateTime CapturedAt,
    string Hash)
{
    /// <summary>创建历史条目，自动计算去重哈希与捕获时间。</summary>
    public static ClipItem Create(
        ClipKind kind, string? text, string? html, string? rtf,
        byte[]? imagePng, IReadOnlyList<string>? files, string preview)
        => new(kind, text, html, rtf, imagePng, files, preview, DateTime.Now,
            ComputeHash(kind, text, html, rtf, imagePng, files));

    /// <summary>按主格式内容计算稳定哈希，用于去重。</summary>
    public static string ComputeHash(
        ClipKind kind, string? text, string? html, string? rtf,
        byte[]? imagePng, IReadOnlyList<string>? files)
    {
        var raw = kind switch
        {
            ClipKind.Text => "t|" + text,
            ClipKind.Html => "h|" + html,
            ClipKind.Rtf => "r|" + rtf,
            ClipKind.Image => "i|" + Convert.ToBase64String(imagePng ?? []),
            ClipKind.Files => "f|" + string.Join('\u0001', files ?? []),
            _ => "?",
        };
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes);
    }
}
