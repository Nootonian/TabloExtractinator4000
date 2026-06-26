using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CommercialCutter;

public static class Cutter
{
    // Builds the non-commercial segments into the output, keeping cuts frame-accurate (unlike
    // the keyframe-snapped fast/hybrid paths). Video and audio are trimmed+concatenated and
    // encoded as two *independent* ffmpeg passes, then stream-copy-muxed together, rather than
    // one combined filter_complex+concat graph mapping both streams to one output.
    //
    // The combined-graph approach was dropping most of the audio track on GPU encodes: NVENC's
    // video encode runs tens of times faster than the CPU-bound AAC audio encode (each segment
    // boundary forces the encoder to flush and restart, and AAC encode just isn't that fast), so
    // within a single muxer, audio packets pile up in the interleaving queue far faster than
    // they're written and silently get dropped once some internal limit is hit — raising
    // -max_muxing_queue_size didn't help, and a finished file with a ~13% complete audio track
    // (verified against a real run) is the result. Encoding each stream in its own pass removes
    // the speed mismatch entirely: nothing is waiting on anything else's pacing.
    public static async Task CutAsync(
        string videoPath, IReadOnlyList<Segment> segments, string outputPath, bool useNvenc,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var keep = segments.Where(s => !s.IsCommercial && s.DurationSeconds > 0.05).ToList();
        if (keep.Count == 0)
            throw new InvalidOperationException("No program segments to keep — check the analysis output.");

        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var outputDuration = keep.Sum(s => s.DurationSeconds);
        var videoCodecArgs = useNvenc
            ? new[] { "-c:v", "h264_nvenc", "-preset", "p4", "-rc", "vbr", "-cq", "20", "-b:v", "0" }
            : new[] { "-c:v", "libx264", "-preset", "fast", "-crf", "20" };

        var workDir = LocalPaths.GetWorkDir(videoPath);
        var tempVideoPath = Path.Combine(workDir, "cut_video.mp4");
        var tempAudioPath = Path.Combine(workDir, "cut_audio.m4a");
        var combinedLog = new StringBuilder();

        try
        {
            var videoFilter = new StringBuilder();
            for (int i = 0; i < keep.Count; i++)
            {
                var s = keep[i];
                videoFilter.Append(
                    $"[0:v]trim=start={s.StartSeconds.ToString(CultureInfo.InvariantCulture)}:end={s.EndSeconds.ToString(CultureInfo.InvariantCulture)},setpts=PTS-STARTPTS[v{i}];");
            }
            for (int i = 0; i < keep.Count; i++) videoFilter.Append($"[v{i}]");
            videoFilter.Append($"concat=n={keep.Count}:v=1:a=0[outv]");

            var videoArgs = new List<string> { "-y", "-i", videoPath, "-filter_complex", videoFilter.ToString(), "-map", "[outv]" };
            videoArgs.AddRange(videoCodecArgs);
            videoArgs.Add(tempVideoPath);

            var videoProgress = new Progress<double>(pct => progress?.Report(pct * 0.7));
            var (_, videoStderr) = await Ffmpeg.RunFfmpegWithProgressAsync(videoArgs, outputDuration, videoProgress, ct);
            combinedLog.AppendLine("=== video pass ===").AppendLine(string.Join(" ", videoArgs)).AppendLine().AppendLine(videoStderr).AppendLine();

            var audioFilter = new StringBuilder();
            for (int i = 0; i < keep.Count; i++)
            {
                var s = keep[i];
                audioFilter.Append(
                    $"[0:a]atrim=start={s.StartSeconds.ToString(CultureInfo.InvariantCulture)}:end={s.EndSeconds.ToString(CultureInfo.InvariantCulture)},asetpts=PTS-STARTPTS,aresample=async=1[a{i}];");
            }
            for (int i = 0; i < keep.Count; i++) audioFilter.Append($"[a{i}]");
            audioFilter.Append($"concat=n={keep.Count}:v=0:a=1[outa]");

            var audioArgs = new[] { "-y", "-i", videoPath, "-filter_complex", audioFilter.ToString(), "-map", "[outa]", "-c:a", "aac", tempAudioPath };
            var audioProgress = new Progress<double>(pct => progress?.Report(70 + pct * 0.2));
            var (_, audioStderr) = await Ffmpeg.RunFfmpegWithProgressAsync(audioArgs, outputDuration, audioProgress, ct);
            combinedLog.AppendLine("=== audio pass ===").AppendLine(string.Join(" ", audioArgs)).AppendLine().AppendLine(audioStderr).AppendLine();

            var muxArgs = new[] { "-y", "-i", tempVideoPath, "-i", tempAudioPath, "-map", "0:v:0", "-map", "1:a:0", "-c", "copy", outputPath };
            var muxProgress = new Progress<double>(pct => progress?.Report(90 + pct * 0.1));
            var (_, muxStderr) = await Ffmpeg.RunFfmpegWithProgressAsync(muxArgs, outputDuration, muxProgress, ct);
            combinedLog.AppendLine("=== mux ===").AppendLine(string.Join(" ", muxArgs)).AppendLine().AppendLine(muxStderr);

            progress?.Report(100.0);
        }
        finally
        {
            WriteFfmpegLog(outputPath, combinedLog.ToString());
            try { File.Delete(tempVideoPath); } catch { }
            try { File.Delete(tempAudioPath); } catch { }
        }
    }

    // The Status box only shows curated progress messages, not ffmpeg's own output — for
    // tracking down issues ffmpeg itself warns about (timestamp gaps, stream sync, etc.) the
    // full stderr from the run goes here too.
    private static void WriteFfmpegLog(string outputPath, string content)
    {
        try
        {
            File.WriteAllText(outputPath + ".ffmpeg.log", content);
        }
        catch
        {
            // Diagnostic-only — never let a logging failure take down the actual cut.
        }
    }

    // Fast path: no re-encoding at all. Every kept segment's start snaps to the nearest
    // preceding keyframe regardless of distance. Equivalent to HybridCutAsync with an
    // infinite snap tolerance.
    public static Task<(int Copied, int Reencoded)> FastCutAsync(
        string videoPath, IReadOnlyList<Segment> segments, IReadOnlyList<double> keyframeTimes, string outputPath,
        IProgress<double>? progress = null, CancellationToken ct = default) =>
        HybridCutAsync(videoPath, segments, keyframeTimes, double.MaxValue, false, outputPath, progress, ct);

    // Per kept segment: if a keyframe lies within `snapToleranceSeconds` of the segment's start,
    // snap to it and stream-copy (fast, no re-encode). Otherwise re-encode just that segment so
    // the cut lands exactly on the analyzed boundary. The pieces are stitched together with the
    // concat demuxer at the end. This trades some of FastCutAsync's speed for tighter cuts on
    // segments where no keyframe happens to be nearby. Returns how many segments were
    // stream-copied vs. re-encoded, for reporting to the user.
    public static async Task<(int Copied, int Reencoded)> HybridCutAsync(
        string videoPath, IReadOnlyList<Segment> segments, IReadOnlyList<double> keyframeTimes,
        double snapToleranceSeconds, bool useNvenc, string outputPath,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var keep = segments.Where(s => !s.IsCommercial && s.DurationSeconds > 0.05).ToList();
        if (keep.Count == 0)
            throw new InvalidOperationException("No program segments to keep — check the analysis output.");

        var workDir = Path.Combine(LocalPaths.GetWorkDir(videoPath), "fastcut_parts");
        if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true);
        Directory.CreateDirectory(workDir);

        var sortedKeyframes = keyframeTimes.OrderBy(t => t).ToList();
        var totalDuration = keep.Sum(s => s.DurationSeconds);
        var listPath = Path.Combine(workDir, "parts.txt");
        var partPaths = new List<string>();
        int copiedCount = 0, reencodedCount = 0;
        var combinedLog = new StringBuilder();

        var videoCodecArgs = useNvenc
            ? new[] { "-c:v", "h264_nvenc", "-preset", "p4", "-rc", "vbr", "-cq", "20", "-b:v", "0" }
            : new[] { "-c:v", "libx264", "-preset", "fast", "-crf", "20" };

        try
        {
            double overallProgressBase = 0;
            for (int i = 0; i < keep.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var s = keep[i];
                var snappedStart = SnapToPrecedingKeyframe(sortedKeyframes, s.StartSeconds);
                var snapDistance = s.StartSeconds - snappedStart;
                var canCopy = snapDistance <= snapToleranceSeconds;

                var partStart = canCopy ? snappedStart : s.StartSeconds;
                var duration = s.EndSeconds - partStart;

                var partPath = Path.Combine(workDir, $"part_{i:D4}.mp4");
                partPaths.Add(partPath);
                if (canCopy) copiedCount++; else reencodedCount++;

                var thisPartShare = (s.EndSeconds - s.StartSeconds) / totalDuration * 100.0;
                var partProgress = new Progress<double>(pct =>
                    progress?.Report(Math.Min(100.0, overallProgressBase + pct / 100.0 * thisPartShare)));

                var args = new List<string>
                {
                    "-y",
                    "-ss", partStart.ToString(CultureInfo.InvariantCulture),
                    "-i", videoPath,
                    "-t", duration.ToString(CultureInfo.InvariantCulture),
                };
                if (canCopy)
                {
                    args.Add("-c"); args.Add("copy");
                }
                else
                {
                    args.AddRange(videoCodecArgs);
                    args.Add("-c:a"); args.Add("aac");
                    args.Add("-max_muxing_queue_size"); args.Add("16384"); // see CutAsync for why
                }
                args.Add("-avoid_negative_ts"); args.Add("make_zero");
                args.Add(partPath);

                var (_, partStderr) = await Ffmpeg.RunFfmpegWithProgressAsync(args, duration, partProgress, ct);
                combinedLog.AppendLine($"=== part {i} ({(canCopy ? "copy" : "re-encode")}) ===")
                           .AppendLine(string.Join(" ", args)).AppendLine().AppendLine(partStderr).AppendLine();

                overallProgressBase += thisPartShare;
            }

            File.WriteAllLines(listPath, partPaths.Select(p => $"file '{Path.GetFullPath(p)}'"));

            var concatArgs = new[]
            {
                "-y",
                "-f", "concat",
                "-safe", "0",
                "-i", listPath,
                "-c", "copy",
                outputPath,
            };
            var (_, concatStderr) = await Ffmpeg.RunFfmpegWithProgressAsync(concatArgs, totalDuration, null, ct);
            combinedLog.AppendLine("=== concat ===").AppendLine(string.Join(" ", concatArgs)).AppendLine().AppendLine(concatStderr);

            progress?.Report(100.0);
            return (copiedCount, reencodedCount);
        }
        finally
        {
            WriteFfmpegLog(outputPath, combinedLog.ToString());
            try { Directory.Delete(workDir, recursive: true); } catch { }
        }
    }

    private static double SnapToPrecedingKeyframe(List<double> sortedKeyframes, double time)
    {
        if (sortedKeyframes.Count == 0) return time;

        int lo = 0, hi = sortedKeyframes.Count - 1, best = 0;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (sortedKeyframes[mid] <= time) { best = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return sortedKeyframes[best] <= time ? sortedKeyframes[best] : time;
    }
}
