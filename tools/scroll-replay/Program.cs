using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OneBox.Plugins.Screenshot.Core;

namespace OneBox.Tools.ScrollReplay;

internal static class Program
{
    // Explicit entry point: WPF imaging must run on an STA thread, not a top-level async Main.
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length is < 1 or > 3)
        {
            Console.Error.WriteLine("Usage: dotnet run --project tools/scroll-replay -- <frame-directory> [output.png] [expected-added-rows.csv|CSV]");
            return 64;
        }

        ScrollStitcher? stitcher = null;
        string? output = null;
        var exitCode = 0;
        var processed = 0;
        var frameCount = 0;
        var limitReached = false;
        var verificationRequested = args.Length == 3;
        try
        {
            var directory = Path.GetFullPath(args[0]);
            output = Path.GetFullPath(args.Length >= 2 ? args[1] : Path.Combine(directory, "replay.png"));
            if (!string.Equals(Path.GetExtension(output), ".png", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Output must have a .png extension.");
            var files = Directory.EnumerateFiles(directory)
                .Where(path => string.Equals(Path.GetExtension(path), ".png", StringComparison.OrdinalIgnoreCase))
                .Where(path => !string.Equals(Path.GetFullPath(path), output, StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal).ToArray();
            frameCount = files.Length;
            if (files.Length == 0) throw new ArgumentException("No input PNG frames found (output path is excluded).");
            var expected = verificationRequested ? ReadExpected(args[2]) : null;
            if (expected is not null && expected.Length != files.Length && expected.Length != files.Length - 1)
                throw new ArgumentException("Expected CSV must contain N values (first = full first-frame height), or N-1 values (subsequent frame deltas).");

            var minMatchRows = ReadMinMatchRows(directory);
            Emit(new { type = "configuration", minMatchRows });
            using var reference = new MemoryStream();
            var width = 0;
            var height = 0;
            for (var index = 0; index < files.Length; index++)
            {
                try
                {
                    var frame = Decode(files[index]);
                    if (index == 0)
                    {
                        width = frame.Width;
                        height = frame.Height;
                        stitcher = new ScrollStitcher(width, int.MaxValue / frame.Stride, minMatchRows);
                    }
                    else if (frame.Width != width || frame.Height != height)
                        throw new InvalidDataException("All frames must have identical pixel dimensions.");

                    var result = stitcher!.Add(frame.Pixels, frame.Height, frame.Stride);
                    processed++;
                    string? validationError = null;
                    int? expectedAdded = null;
                    var failed = result.Status != ScrollAppendStatus.First && result.Status != ScrollAppendStatus.Appended
                        && result.Status != ScrollAppendStatus.NoChange && result.Status != ScrollAppendStatus.LimitReached;
                    if (expected is not null)
                    {
                        expectedAdded = expected.Length == files.Length ? expected[index] : index == 0 ? height : expected[index - 1];
                        validationError = Validate(stitcher, reference, frame, index, expectedAdded.Value, result);
                    }
                    Emit(new
                    {
                        type = "frame", index, file = Path.GetFileName(files[index]), status = result.Status.ToString(),
                        appended = result.AppendedRows, overlap = result.OverlapRows,
                        stitcher.LastScrollDelta, stitcher.LastMatchedRows, stitcher.LastRequiredRows,
                        diagnostics = stitcher.LastDiagnostics,
                        totalHeight = stitcher.TotalHeight, expectedAdded,
                        pixelsVerified = expected is not null && validationError is null,
                        error = validationError
                    });
                    if (failed || validationError is not null)
                    {
                        exitCode = validationError is null ? 1 : 2;
                        break; // Never feed a later frame after a failed alignment or verification.
                    }
                    if (result.Status == ScrollAppendStatus.LimitReached)
                    {
                        limitReached = true;
                        break;
                    }
                }
                catch (Exception exception)
                {
                    Emit(new
                    {
                        type = "frame", index, file = Path.GetFileName(files[index]), status = "Error",
                        appended = 0, overlap = 0,
                        LastScrollDelta = stitcher?.LastScrollDelta,
                        LastMatchedRows = stitcher?.LastMatchedRows,
                        LastRequiredRows = stitcher?.LastRequiredRows,
                        error = exception.Message
                    });
                    exitCode = 1;
                    break;
                }
            }
        }
        catch (Exception exception)
        {
            Emit(new { type = "error", error = exception.Message });
            exitCode = 1;
        }

        var saved = false;
        if (stitcher is { TotalHeight: > 0 } && output is not null)
        {
            try
            {
                var bitmap = BitmapSource.Create(stitcher.Stride / 4, stitcher.TotalHeight, 96, 96,
                    PixelFormats.Bgra32, null, stitcher.BuildBgra(), stitcher.Stride);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                using var stream = File.Create(output);
                encoder.Save(stream);
                saved = true;
            }
            catch (Exception exception)
            {
                Emit(new { type = "error", error = "PNG export failed: " + exception.Message });
                exitCode = 1;
            }
        }
        Emit(new
        {
            type = "summary", exitCode, processed, frameCount, output = saved ? output : null,
            totalHeight = stitcher?.TotalHeight ?? 0, partial = exitCode != 0 || processed != frameCount || limitReached,
            limitReached, verificationRequested, bottomConfirmed = false
        });
        return exitCode;
    }

    private static Frame Decode(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        BitmapSource bitmap = decoder.Frames[0];
        if (bitmap.Format != PixelFormats.Bgra32)
            bitmap = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var stride = checked(bitmap.PixelWidth * 4);
        var pixels = new byte[checked(stride * bitmap.PixelHeight)];
        bitmap.CopyPixels(pixels, stride, 0);
        return new Frame(bitmap.PixelWidth, bitmap.PixelHeight, stride, pixels);
    }

    private static int ReadMinMatchRows(string directory)
    {
        var path = Path.Combine(directory, "session.json");
        if (!File.Exists(path)) return 24;
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        if (!json.RootElement.TryGetProperty("minMatchRows", out var value)) return 24;
        if (!value.TryGetInt32(out var rows) || rows <= 0)
            throw new InvalidDataException("session.json minMatchRows must be a positive integer.");
        return rows;
    }

    private static int[] ReadExpected(string argument)
    {
        var csv = File.Exists(argument) ? File.ReadAllText(argument) : argument;
        var tokens = csv.Trim().TrimStart('\uFEFF').Split([',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return tokens.Select(token => int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value : throw new ArgumentException("Expected CSV contains a non-negative integer error: " + token)).ToArray();
    }

    private static string? Validate(ScrollStitcher stitcher, MemoryStream reference, Frame frame, int index,
        int added, ScrollAppendResult result)
    {
        if (added < 0 || added > frame.Height || (index == 0 && added != frame.Height))
            return "Expected added rows are outside the valid frame range; first frame must use its full height.";
        if (result.AppendedRows != added)
            return $"Added-row mismatch: expected {added}, actual {result.AppendedRows}.";
        if (index > 0)
        {
            var overlapBytes = checked((frame.Height - added) * frame.Stride);
            if (overlapBytes > reference.Length)
                return "Expected overlap exceeds the already accepted reference image.";
            var tailStart = checked((int)reference.Length - overlapBytes);
            if (!reference.GetBuffer().AsSpan(tailStart, overlapBytes).SequenceEqual(frame.Pixels.AsSpan(0, overlapBytes)))
                return "Full-pixel overlap mismatch (BGRA, all columns/channels; no tolerance).";
        }
        var addedBytes = checked(added * frame.Stride);
        reference.Write(frame.Pixels, frame.Pixels.Length - addedBytes, addedBytes);
        if (!stitcher.BuildBgra().AsSpan().SequenceEqual(reference.GetBuffer().AsSpan(0, checked((int)reference.Length))))
            return "Full-pixel accumulated image mismatch (BGRA, all rows/columns/channels).";
        return null;
    }

    private static void Emit(object value) => Console.WriteLine(JsonSerializer.Serialize(value));
    private sealed record Frame(int Width, int Height, int Stride, byte[] Pixels);
}
