using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace CommercialCutter;

public static class Cataloger
{
    // Extracts cropped, downscaled thumbnails of the logo-bug region at a fixed interval.
    // Filenames are frame_000001.jpg, frame_000002.jpg, ... each `intervalSeconds` apart,
    // so frame index N corresponds to timestamp (N-1) * intervalSeconds.
    public static async Task CatalogAsync(
        string videoPath, CropRect crop, string outDir, double intervalSeconds,
        double? sourceDurationSeconds = null, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(outDir);
        foreach (var f in Directory.GetFiles(outDir, "frame_*.jpg")) File.Delete(f);

        var fps = 1.0 / intervalSeconds;
        var vf  = $"crop={crop.Width}:{crop.Height}:{crop.X}:{crop.Y},fps={fps.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

        var args = new[]
        {
            "-y",
            "-i", videoPath,
            "-vf", vf,
            "-q:v", "2",
            Path.Combine(outDir, "frame_%06d.jpg"),
        };

        // Progress is measured against *source* playback time (ffmpeg's out_time_us advances
        // at source rate here, not output-frame count), so pass the source duration.
        await Ffmpeg.RunFfmpegWithProgressAsync(args, sourceDurationSeconds ?? 0, progress, ct);
    }

    // Extracts a single cropped frame to use as the "logo present" reference image for `analyze`.
    public static async Task<string> CatalogReferenceCropAsync(
        string videoPath, double atSeconds, CropRect crop, string outPath, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(outPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var vf = $"crop={crop.Width}:{crop.Height}:{crop.X}:{crop.Y}";
        var (code, _, stderr) = await Ffmpeg.RunFfmpegAsync(new[]
        {
            "-y",
            "-ss", atSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-i", videoPath,
            "-vf", vf,
            "-frames:v", "1",
            "-q:v", "2",
            outPath,
        }, ct);

        if (code != 0)
            throw new InvalidOperationException($"ffmpeg reference crop failed: {stderr}");

        return outPath;
    }

    public static async Task<string> ExtractSingleFrameAsync(
        string videoPath, double atSeconds, string outPath, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(outPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var (code, _, stderr) = await Ffmpeg.RunFfmpegAsync(new[]
        {
            "-y",
            "-ss", atSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-i", videoPath,
            "-frames:v", "1",
            "-q:v", "2",
            outPath,
        }, ct);

        if (code != 0)
            throw new InvalidOperationException($"ffmpeg frame extract failed: {stderr}");

        return outPath;
    }
}
