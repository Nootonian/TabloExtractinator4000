using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CommercialCutter;

public static class Ffmpeg
{
    public static string FfmpegPath  => Resolve("ffmpeg.exe", "ffmpeg");
    public static string FfprobePath => Resolve("ffprobe.exe", "ffprobe");

    // Looks next to this exe first (so it can share the main app's tools\ folder if copied there),
    // then falls back to the bare name and lets the OS search PATH.
    private static string Resolve(string exeName, string pathName)
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, exeName),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "TabloExtractinator4000", "tools", exeName),
            Path.Combine(AppContext.BaseDirectory, "tools", exeName),
        };
        foreach (var c in candidates)
            if (File.Exists(c)) return Path.GetFullPath(c);
        return pathName;
    }

    public static async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(
        string exePath, IEnumerable<string> args, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(exePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)!;
        try
        {
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            return (proc.ExitCode, await stdoutTask, await stderrTask);
        }
        finally
        {
            if (!proc.HasExited)
                try { proc.Kill(entireProcessTree: true); } catch { }
        }
    }

    public static Task<(int ExitCode, string StdOut, string StdErr)> RunFfmpegAsync(
        IEnumerable<string> args, CancellationToken ct = default) => RunAsync(FfmpegPath, args, ct);

    public static Task<(int ExitCode, string StdOut, string StdErr)> RunFfprobeAsync(
        IEnumerable<string> args, CancellationToken ct = default) => RunAsync(FfprobePath, args, ct);

    // Runs ffmpeg with `-progress pipe:1`, reporting fractional completion (0-1) against
    // expectedDurationSeconds of *output* media as it's produced. Returns the captured
    // stderr too, since some callers (e.g. blackdetect/silencedetect) need to parse it.
    public static async Task<(int ExitCode, string StdErr)> RunFfmpegWithProgressAsync(
        IEnumerable<string> args, double expectedDurationSeconds,
        IProgress<double>? progress, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(FfmpegPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add("-progress"); psi.ArgumentList.Add("pipe:1");

        using var proc = Process.Start(psi)!;
        var stderr = new System.Text.StringBuilder();
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
        proc.BeginErrorReadLine();

        try
        {
            string? line;
            while ((line = await proc.StandardOutput.ReadLineAsync(ct)) != null)
            {
                if (line.StartsWith("out_time_us=", StringComparison.Ordinal) &&
                    long.TryParse(line[12..], out var outTimeUs) && expectedDurationSeconds > 0)
                {
                    var pct = Math.Min(100.0, outTimeUs / 1_000_000.0 / expectedDurationSeconds * 100.0);
                    progress?.Report(pct);
                }
            }
            await proc.WaitForExitAsync(ct);
        }
        finally
        {
            if (!proc.HasExited)
                try { proc.Kill(entireProcessTree: true); } catch { }
        }

        if (proc.ExitCode != 0 && !ct.IsCancellationRequested)
            throw new InvalidOperationException($"ffmpeg exited {proc.ExitCode}.\nstderr:\n{stderr}");

        progress?.Report(100.0);
        return (proc.ExitCode, stderr.ToString());
    }

    // Runs blackdetect (video) and silencedetect (audio) in a single decode pass and parses
    // the resulting intervals from stderr. This decodes the entire file (no output is written),
    // so it costs roughly one playthrough — there's no way around that with ffmpeg's filters.
    public static async Task<(List<(double Start, double End)> Black, List<(double Start, double End)> Silence)>
        DetectBlackAndSilenceAsync(string videoPath, double durationSeconds, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        // d=1.0 (minimum duration) on silencedetect matters a lot: at 0.3s it flags ordinary
        // dialogue pauses and dramatic beats constantly, which then falsely "corroborate" breaks
        // that aren't real. A genuine ad-break dead-air bumper is reliably 1s+ of true silence;
        // a quiet line reading rarely is. -35dB (vs the default -30dB) similarly excludes scenes
        // that are merely quiet, not dead silent.
        var args = new[]
        {
            "-i", videoPath,
            "-vf", "blackdetect=d=0.1:pic_th=0.98:pix_th=0.10",
            "-af", "silencedetect=n=-35dB:d=1.0",
            "-f", "null", "-",
        };

        var (_, stderr) = await RunFfmpegWithProgressAsync(args, durationSeconds, progress, ct);

        var black = new List<(double Start, double End)>();
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
            stderr, @"black_start:(?<s>[\d.]+)\s+black_end:(?<e>[\d.]+)"))
        {
            black.Add((
                double.Parse(m.Groups["s"].Value, System.Globalization.CultureInfo.InvariantCulture),
                double.Parse(m.Groups["e"].Value, System.Globalization.CultureInfo.InvariantCulture)));
        }

        var starts = System.Text.RegularExpressions.Regex.Matches(stderr, @"silence_start:\s*(?<s>[\d.]+)")
            .Select(m => double.Parse(m.Groups["s"].Value, System.Globalization.CultureInfo.InvariantCulture)).ToList();
        var ends = System.Text.RegularExpressions.Regex.Matches(stderr, @"silence_end:\s*(?<e>[\d.]+)")
            .Select(m => double.Parse(m.Groups["e"].Value, System.Globalization.CultureInfo.InvariantCulture)).ToList();

        var silence = new List<(double Start, double End)>();
        for (int i = 0; i < Math.Min(starts.Count, ends.Count); i++)
            silence.Add((starts[i], ends[i]));

        return (black, silence);
    }

    // Checks whether this ffmpeg build can use the NVIDIA NVENC H.264 encoder
    // (present on RTX/GTX cards with up-to-date drivers).
    public static async Task<bool> IsNvencAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            var (code, stdout, _) = await RunFfmpegAsync(new[] { "-hide_banner", "-encoders" }, ct);
            return code == 0 && stdout.Contains("h264_nvenc", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    // Returns the presentation timestamps (seconds) of every keyframe in the video's first
    // video stream. Useful for snapping cut points so a stream-copy export doesn't need to
    // re-encode the frames it keeps.
    public static async Task<List<double>> GetKeyframeTimestampsAsync(string videoPath, CancellationToken ct = default)
    {
        var (code, stdout, stderr) = await RunFfprobeAsync(new[]
        {
            "-v", "error",
            "-skip_frame", "nokey",
            "-select_streams", "v:0",
            "-show_entries", "frame=pts_time",
            "-of", "csv=p=0",
            videoPath,
        }, ct);

        if (code != 0)
            throw new InvalidOperationException($"ffprobe keyframe scan failed: {stderr.Trim()}");

        var result = new List<double>();
        foreach (var line in stdout.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0 && double.TryParse(trimmed, System.Globalization.CultureInfo.InvariantCulture, out var t))
                result.Add(t);
        }
        return result;
    }

    public static async Task<double> GetDurationSecondsAsync(string videoPath, CancellationToken ct = default)
    {
        var (code, stdout, stderr) = await RunFfprobeAsync(new[]
        {
            "-v", "error",
            "-show_entries", "format=duration",
            "-of", "default=noprint_wrappers=1:nokey=1",
            videoPath,
        }, ct);

        if (code != 0)
            throw new InvalidOperationException($"ffprobe failed: {stderr.Trim()}");

        return double.Parse(stdout.Trim(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
