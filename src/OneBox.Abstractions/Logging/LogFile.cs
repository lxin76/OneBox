using System.IO;
using System.Text;

namespace OneBox.Abstractions.Logging;

/// <summary>
/// 单文件日志写入器：一个日志只对应一个文件，达到大小上限后丢弃最旧内容再继续追加。
///
/// 与“按日期/大小轮转成多个文件”不同，这里永远只有一个文件；与“环形回绕”不同，
/// 文件内容始终保持时间顺序（最新在末尾），可以直接用编辑器打开排障。
/// 达到 <see cref="MaxBytes"/> 时只保留尾部一段（默认最近 25%），并回退到完整行边界，
/// 再用临时文件原子替换，避免读取方看到半截文件。
/// </summary>
public sealed class LogFile
{
    /// <summary>默认上限：10 MB。</summary>
    public const long DefaultMaxBytes = 10L * 1024 * 1024;

    /// <summary>单行上限：异常堆栈等超长内容会被截断，避免一行撑满整个文件。</summary>
    private const int MaxLineBytes = 64 * 1024;

    /// <summary>超限时保留的尾部比例。</summary>
    private const double RetainRatio = 0.25;

    private readonly object _sync = new();
    private readonly string _path;
    private readonly long _maxBytes;

    /// <param name="path">日志文件路径（目录不存在时自动创建）。</param>
    /// <param name="maxBytes">文件大小上限，默认 10 MB。</param>
    public LogFile(string path, long maxBytes = DefaultMaxBytes)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("日志路径不能为空。", nameof(path));
        if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes), "日志大小上限必须大于 0。");
        _path = path;
        _maxBytes = maxBytes;
    }

    /// <summary>日志文件路径。</summary>
    public string Path => _path;

    /// <summary>文件大小上限（字节）。</summary>
    public long MaxBytes => _maxBytes;

    /// <summary>追加一条日志。写入失败时静默，绝不向调用方抛异常。</summary>
    public void Write(string tag, string message)
    {
        try
        {
            var bytes = Format(tag, message);
            lock (_sync)
            {
                var directory = System.IO.Path.GetDirectoryName(_path);
                if (directory is { Length: > 0 }) Directory.CreateDirectory(directory);

                if (File.Exists(_path) && new FileInfo(_path).Length + bytes.Length > _maxBytes)
                    Trim(bytes.Length);

                using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                stream.Write(bytes, 0, bytes.Length);
            }
        }
        catch
        {
            // 日志写入失败不影响主流程
        }
    }

    private byte[] Format(string tag, string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{tag}] {message}{Environment.NewLine}";
        var limit = (int)Math.Min(MaxLineBytes, Math.Min(_maxBytes, int.MaxValue));
        if (Encoding.UTF8.GetByteCount(line) > limit) line = Clip(line, limit);
        return Encoding.UTF8.GetBytes(line);
    }

    /// <summary>按字符边界截断超长行（避免把多字节字符截成半个）。</summary>
    private static string Clip(string line, int limit)
    {
        var suffix = $"…（单行超长，已截断）{Environment.NewLine}";
        var budget = Math.Max(0, limit - Encoding.UTF8.GetByteCount(suffix));

        // 二分找到“最长的、UTF-8 字节数不超过 budget 的前缀”
        var low = 0;
        var high = line.Length;
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            if (Encoding.UTF8.GetByteCount(line.AsSpan(0, mid)) <= budget) low = mid;
            else high = mid - 1;
        }
        return line[..low] + suffix;
    }

    /// <summary>丢弃最旧内容，只保留尾部一段，并给即将写入的内容留出空间。</summary>
    private void Trim(int incomingBytes)
    {
        var length = new FileInfo(_path).Length;
        var target = _maxBytes - incomingBytes;

        // 保留区不能超过“追加后仍不超上限”的余量；余量不足时直接清空。
        var keep = (int)Math.Min(Math.Min((long)(_maxBytes * RetainRatio), target), length);
        if (keep <= 0)
        {
            Clear();
            return;
        }

        var start = length - keep;

        byte[] tail;
        using (var reader = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            reader.Seek(start, SeekOrigin.Begin);
            tail = new byte[keep];
            var read = 0;
            while (read < tail.Length)
            {
                var count = reader.Read(tail, read, tail.Length - read);
                if (count <= 0) break;
                read += count;
            }
            if (read < tail.Length) Array.Resize(ref tail, read);
        }

        // 起点可能落在行中间：丢掉残行，保证保留内容从完整的一行开始。
        var newline = Array.IndexOf(tail, (byte)'\n');
        var offset = newline >= 0 ? newline + 1 : 0;

        if (offset >= tail.Length)
        {
            // 保留区里没有完整行（极端情况）：清空文件，下一次写入从空文件开始。
            Clear();
            return;
        }

        var temp = _path + ".tmp";
        try
        {
            File.WriteAllBytes(temp, tail[offset..]);
            File.Move(temp, _path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    private void Clear()
    {
        using var stream = new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
    }
}
