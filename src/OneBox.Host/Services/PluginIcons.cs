using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OneBox.Abstractions;

namespace OneBox.Host.Services;

/// <summary>
/// 插件图标解析：拿到界面可以直接绑定的位图。
///
/// 取图顺序：
///   1. 清单声明的 icon 文件（可选，留作第三方插件自由发挥）
///   2. 插件 exe 里嵌入的图标（由项目里的 ApplicationIcon 决定）
///   3. 通用占位图标（芯片造型）
///
/// 以 exe 为主是有意的：图标本来就随 exe 一起分发，宿主直接读它就永远不会
/// 和插件实际长相脱节，插件作者也不必额外维护一个图标文件。
///
/// 图标纯属展示用的装饰，任何一步失败都只降级、不抛异常，
/// 免得一个坏掉的图标把整份插件列表拖垮。
/// </summary>
public static class PluginIcons
{
    private static readonly Dictionary<string, ImageSource> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static ImageSource? _fallback;
    private static ImageSource? _host;

    /// <summary>未声明图标（且 exe 里也没有）时使用的通用占位图标。</summary>
    public static ImageSource Fallback => _fallback ??= LoadPack("Assets/plugin-generic.png") ?? CreatePlaceholder();

    /// <summary>宿主自身的图标（设置中心列表里"宿主"那一项用）。</summary>
    public static ImageSource Host => _host ??= LoadPack("Assets/app.png") ?? Fallback;

    /// <summary>
    /// 解析某个插件的图标。任何失败都退回通用占位图标。
    /// </summary>
    public static ImageSource ForManifest(PluginManifest? manifest)
    {
        if (manifest is null) return Fallback;

        var key = string.IsNullOrWhiteSpace(manifest.Directory) ? manifest.Id : manifest.Directory;
        if (Cache.TryGetValue(key, out var cached)) return cached;

        var image = Resolve(manifest);
        Cache[key] = image;
        return image;
    }

    /// <summary>图标文件变化（安装/卸载插件）后清空缓存。</summary>
    public static void Invalidate()
    {
        Cache.Clear();
        _fallback = null;
        _host = null;
    }

    private static ImageSource Resolve(PluginManifest manifest)
    {
        // 1. 清单显式声明的图标
        if (!string.IsNullOrWhiteSpace(manifest.Icon) && !string.IsNullOrWhiteSpace(manifest.Directory))
        {
            var declared = Path.GetFullPath(Path.Combine(manifest.Directory, manifest.Icon));
            if (LoadIconFile(declared) is { } custom) return custom;
        }

        // 2. 插件 exe 自带的图标
        if (!string.IsNullOrWhiteSpace(manifest.Directory) && !string.IsNullOrWhiteSpace(manifest.Entry))
        {
            var exe = Path.GetFullPath(Path.Combine(manifest.Directory, manifest.Entry));
            if (ExtractFromExecutable(exe) is { } embedded) return embedded;
        }

        return Fallback;
    }

    private static ImageSource? LoadIconFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;

            // 从 .ico 里取最大的一帧，缩放后列表里才不糊
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var decoder = new IconBitmapDecoder(
                stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);

            BitmapFrame? best = null;
            foreach (var frame in decoder.Frames)
            {
                if (best is null || frame.PixelWidth > best.PixelWidth) best = frame;
            }

            if (best is null) return null;

            var frozen = BitmapFrame.Create(best);
            frozen.Freeze();
            return frozen;
        }
        catch (Exception ex)
        {
            HostLog.Write($"插件图标读取失败（{path}）: {ex.Message}");
            return null;
        }
    }

    // ---- 从 exe 读取嵌入图标 ----

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, IntPtr[] large, IntPtr[] small, uint count);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    /// <summary>
    /// 取 exe 里嵌入的第一枚图标。
    /// 用 ExtractIconEx 而不是 System.Drawing：这里只需要一个句柄，
    /// 转成 WPF 位图即可，省掉一个 Windows-only 的额外依赖。
    /// </summary>
    private static ImageSource? ExtractFromExecutable(string exePath)
    {
        var large = new IntPtr[1];
        var small = new IntPtr[1];

        try
        {
            if (!File.Exists(exePath)) return null;

            var count = ExtractIconEx(exePath, 0, large, small, 1);
            var handle = large[0] != IntPtr.Zero ? large[0] : small[0];
            if (count == 0 || handle == IntPtr.Zero) return null;

            var source = Imaging.CreateBitmapSourceFromHIcon(
                handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch (Exception ex)
        {
            HostLog.Write($"插件 exe 图标提取失败（{exePath}）: {ex.Message}");
            return null;
        }
        finally
        {
            // ExtractIconEx 返回的句柄由调用方负责释放
            if (large[0] != IntPtr.Zero) DestroyIcon(large[0]);
            if (small[0] != IntPtr.Zero) DestroyIcon(small[0]);
        }
    }

    private static ImageSource? LoadPack(string relative)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri($"pack://application:,,,/{relative}", UriKind.Absolute);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            // pack 资源缺失时由调用方降级，不阻断宿主
            return null;
        }
    }

    /// <summary>连占位图标都加载不了时的兜底：画一块纯色方块。</summary>
    private static ImageSource CreatePlaceholder()
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRoundedRectangle(
                new SolidColorBrush(Color.FromRgb(0x3A, 0x42, 0x58)), null,
                new Rect(0, 0, 32, 32), 8, 8);
        }

        var bitmap = new RenderTargetBitmap(32, 32, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
