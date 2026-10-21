namespace OneBox.Plugins.Recorder.Core;

/// <summary>录制文件名生成。固定用日期时间，便于按时间排序，也不需要用户配置模板。</summary>
public static class RecordingFileName
{
    public const string Extension = ".mp4";

    /// <summary>形如 录屏_20261021_093623。</summary>
    public static string BuildBaseName(DateTime time) => $"录屏_{time:yyyyMMdd_HHmmss}";

    /// <summary>
    /// 在目录里挑一个不冲突的文件名。冲突时追加 _2、_3…
    /// 传入 <paramref name="exists"/> 而不是直接查文件系统，便于单测覆盖冲突分支。
    /// </summary>
    public static string EnsureUnique(string directory, string baseName, Func<string, bool> exists)
    {
        for (var i = 1; i < 1000; i++)
        {
            var name = i == 1 ? baseName + Extension : $"{baseName}_{i}{Extension}";
            if (!exists(Path.Combine(directory, name))) return name;
        }

        // 同一秒内连续开始上千次录制不现实，走到这里只做兜底，避免死循环。
        return $"{baseName}_{DateTime.Now.Ticks}{Extension}";
    }
}
