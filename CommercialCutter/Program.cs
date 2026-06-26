using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace CommercialCutter;

public static class Program
{
    // Built as WinExe so double-clicking the exe doesn't pop a console window alongside the
    // GUI. CLI commands still need somewhere to print to, so they attach to the launching
    // console (if run from one) or allocate a fresh one (e.g. double-clicked with args via a
    // shortcut) before printing anything.
    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int processId);
    [DllImport("kernel32.dll")] private static extern bool AllocConsole();
    private const int ATTACH_PARENT_PROCESS = -1;

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            var app = new System.Windows.Application();
            app.Run(new MainWindow());
            return 0;
        }

        if (!AttachConsole(ATTACH_PARENT_PROCESS)) AllocConsole();
        return RunCliAsync(args).GetAwaiter().GetResult();
    }

    private static async Task<int> RunCliAsync(string[] args)
    {
        try
        {
            switch (args[0])
            {
                case "selectbox":
                    return await SelectBoxAsync(args[1..]);
                case "catalog":
                    return await CatalogAsync(args[1..]);
                case "analyze":
                    return await AnalyzeAsync(args[1..]);
                case "cut":
                    return await CutAsync(args[1..]);
                case "fastcut":
                    return await FastCutAsync(args[1..]);
                case "eval":
                    return await EvalAsync(args[1..]);
                default:
                    PrintUsage();
                    return 1;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            commercialcutter selectbox <video> <config.json>
                Pick a sample frame, draw a box around the station logo bug, and save it
                along with a reference crop image next to config.json.

            commercialcutter catalog <video> <config.json> <thumbsDir> [intervalSeconds=1]
                Extract cropped thumbnails of the logo region at a fixed interval.

            commercialcutter analyze <config.json> <thumbsDir> <segments.json> [intervalSeconds=1] [threshold=0.92] [refineVideoPath] [maxNudgeSeconds=6]
                Score thumbnails against the reference logo and produce a program/commercial segment list.
                If refineVideoPath is given, also scans that video for black/silent dips (full decode
                pass) and nudges each cut onto the nearest one within maxNudgeSeconds, so fade-to-black
                bumpers stay attached to the program side instead of getting clipped mid-fade.

            commercialcutter cut <video> <segments.json> <output.mp4>
                Re-encode the video, keeping only the non-commercial segments.
                Uses h264_nvenc if available, falling back to libx264.

            commercialcutter fastcut <video> <segments.json> <output.mp4>
                Like `cut`, but stream-copies instead of re-encoding (much faster). Each kept
                segment's start snaps to the nearest preceding keyframe, so a segment may keep
                up to one GOP of footage from just before the boundary.

            commercialcutter eval <video> <config.json> <thumbsDir> <groundTruthTsv> <intervalSeconds> <expectedMinutes>
                Scores the adaptive auto-search pipeline against a manually-cut ground-truth TSV
                (LosslessCut export: Start/End/Name columns). Reuses already-cataloged thumbnails
                and caches the black/silence scan next to the video, so repeated runs are fast —
                meant for tuning the analyzer from the command line without a GUI round-trip.
            """);
    }

    private static async Task<int> SelectBoxAsync(string[] a)
    {
        if (a.Length < 2) { Console.Error.WriteLine("usage: selectbox <video> <config.json>"); return 1; }
        var video = a[0];
        var configPath = a[1];

        var duration = await Ffmpeg.GetDurationSecondsAsync(video);
        var sampleAt = duration * 0.25; // a quarter into the recording, well clear of any opening black/title card

        var configDir = Path.GetDirectoryName(Path.GetFullPath(configPath))!;
        var framePath = Path.Combine(configDir, "selectbox_frame.jpg");
        await Cataloger.ExtractSingleFrameAsync(video, sampleAt, framePath);

        var app = new System.Windows.Application();
        var window = new BoxSelectWindow(video, framePath, sampleAt, duration);
        app.Run(window);
        var crop = window.Result;

        if (crop is null)
        {
            Console.WriteLine("Cancelled — no box selected.");
            return 1;
        }

        // Save the reference crop (logo-present) alongside the config for `analyze` to compare against.
        var referencePath = Path.Combine(configDir, "reference_logo.jpg");
        await Cataloger.CatalogReferenceCropAsync(video, sampleAt, crop, referencePath);

        var (w, h) = await GetFrameSizeAsync(video);
        var config = new CutterConfig(crop, referencePath, w, h);
        ConfigStore.SaveConfig(configPath, config);

        Console.WriteLine($"Saved config to {configPath}");
        Console.WriteLine($"Crop: x={crop.X} y={crop.Y} w={crop.Width} h={crop.Height}");
        return 0;
    }

    private static async Task<(int Width, int Height)> GetFrameSizeAsync(string video)
    {
        var (code, stdout, stderr) = await Ffmpeg.RunFfprobeAsync(new[]
        {
            "-v", "error",
            "-select_streams", "v:0",
            "-show_entries", "stream=width,height",
            "-of", "csv=p=0",
            video,
        });
        if (code != 0) throw new InvalidOperationException($"ffprobe failed: {stderr}");
        var parts = stdout.Trim().Split(',');
        return (int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture));
    }

    private static async Task<int> CatalogAsync(string[] a)
    {
        if (a.Length < 3) { Console.Error.WriteLine("usage: catalog <video> <config.json> <thumbsDir> [intervalSeconds=1]"); return 1; }
        var video = a[0];
        var config = ConfigStore.LoadConfig(a[1]);
        var thumbsDir = a[2];
        var interval = a.Length > 3 ? double.Parse(a[3], CultureInfo.InvariantCulture) : 1.0;

        Console.WriteLine("Cataloging thumbnails...");
        await Cataloger.CatalogAsync(video, config.Crop, thumbsDir, interval);
        Console.WriteLine($"Done. Thumbnails in {thumbsDir}");
        return 0;
    }

    private static async Task<int> AnalyzeAsync(string[] a)
    {
        if (a.Length < 3)
        {
            Console.Error.WriteLine(
                "usage: analyze <config.json> <thumbsDir> <segments.json> [intervalSeconds=1] [threshold=0.92] " +
                "[refineVideoPath] [maxNudgeSeconds=6]");
            return 1;
        }
        var config = ConfigStore.LoadConfig(a[0]);
        var thumbsDir = a[1];
        var segmentsPath = a[2];
        var interval = a.Length > 3 ? double.Parse(a[3], CultureInfo.InvariantCulture) : 1.0;
        var threshold = a.Length > 4 ? double.Parse(a[4], CultureInfo.InvariantCulture) : 0.92;
        var refineVideoPath = a.Length > 5 ? a[5] : null;
        var maxNudgeSeconds = a.Length > 6 ? double.Parse(a[6], CultureInfo.InvariantCulture) : 6.0;

        var scores = Analyzer.ScoreThumbnails(thumbsDir, config.ReferenceImagePath, interval);
        var segments = Analyzer.BuildSegments(scores, interval, threshold);

        if (refineVideoPath is not null)
        {
            Console.WriteLine("Scanning for black/silent transitions (full decode pass)...");
            var duration = await Ffmpeg.GetDurationSecondsAsync(refineVideoPath);
            var (black, silence) = await Ffmpeg.DetectBlackAndSilenceAsync(refineVideoPath, duration);
            segments = Analyzer.ValidateBreaksAgainstBumpers(segments, black, silence);
            segments = Analyzer.RefineBoundariesWithBlackAndSilence(segments, black, silence, maxNudgeSeconds);
            var bridged = Analyzer.FindBlackBridgedBreaks(black);
            segments = Analyzer.MergeBlackBridgedBreaks(segments, bridged);
            Console.WriteLine($"Found {black.Count} black dip(s), {silence.Count} silent dip(s).");
        }

        ConfigStore.SaveSegments(segmentsPath, segments);

        var commercialTotal = segments.Where(s => s.IsCommercial).Sum(s => s.DurationSeconds);
        var programTotal = segments.Where(s => !s.IsCommercial).Sum(s => s.DurationSeconds);
        Console.WriteLine($"{segments.Count} segments — program {programTotal:F0}s, commercial {commercialTotal:F0}s");
        foreach (var s in segments)
            Console.WriteLine($"  [{(s.IsCommercial ? "AD " : "PRG")}] {s.StartSeconds:F1}s - {s.EndSeconds:F1}s ({s.DurationSeconds:F1}s)");

        return 0;
    }

    private static async Task<int> CutAsync(string[] a)
    {
        if (a.Length < 3) { Console.Error.WriteLine("usage: cut <video> <segments.json> <output.mp4>"); return 1; }
        var video = a[0];
        var segments = ConfigStore.LoadSegments(a[1]);
        var output = a[2];

        var useNvenc = await Ffmpeg.IsNvencAvailableAsync();
        Console.WriteLine(useNvenc ? "Cutting (NVENC hardware encode)..." : "Cutting (libx264 software encode)...");
        await Cutter.CutAsync(video, segments, output, useNvenc);
        Console.WriteLine($"Done. Output: {output}");
        return 0;
    }

    private static async Task<int> FastCutAsync(string[] a)
    {
        if (a.Length < 3) { Console.Error.WriteLine("usage: fastcut <video> <segments.json> <output.mp4>"); return 1; }
        var video = a[0];
        var segments = ConfigStore.LoadSegments(a[1]);
        var output = a[2];

        Console.WriteLine("Scanning keyframes...");
        var keyframes = await Ffmpeg.GetKeyframeTimestampsAsync(video);

        Console.WriteLine("Cutting (stream copy)...");
        await Cutter.FastCutAsync(video, segments, keyframes, output);
        Console.WriteLine($"Done. Output: {output}");
        return 0;
    }

    // Scores the current detection pipeline against a manually-cut ground-truth TSV (LosslessCut
    // export format), reusing already-cataloged thumbnails and a cached black/silence scan so
    // repeated iterations don't have to pay for cataloging or the full decode pass again. This is
    // for tuning the analyzer directly from the command line, without going through the GUI.
    private static async Task<int> EvalAsync(string[] a)
    {
        if (a.Length < 6)
        {
            Console.Error.WriteLine("usage: eval <video> <config.json> <thumbsDir> <groundTruthTsv> <intervalSeconds> <expectedMinutes>");
            return 1;
        }
        var video = a[0];
        var config = ConfigStore.LoadConfig(a[1]);
        var thumbsDir = a[2];
        var tsvPath = a[3];
        var interval = double.Parse(a[4], CultureInfo.InvariantCulture);
        var expectedMinutes = double.Parse(a[5], CultureInfo.InvariantCulture);

        var duration = await Ffmpeg.GetDurationSecondsAsync(video);

        var cachePath = video + ".blacksilence.cache.json";
        List<(double Start, double End)> black, silence;
        if (File.Exists(cachePath))
        {
            Console.WriteLine("Loading cached black/silence intervals...");
            var cached = System.Text.Json.JsonSerializer.Deserialize<CachedBlackSilence>(File.ReadAllText(cachePath))!;
            black = cached.Black.Select(x => (x[0], x[1])).ToList();
            silence = cached.Silence.Select(x => (x[0], x[1])).ToList();
        }
        else
        {
            Console.WriteLine("Scanning black/silence (full decode pass — will be cached for next time)...");
            (black, silence) = await Ffmpeg.DetectBlackAndSilenceAsync(video, duration);
            var toSave = new CachedBlackSilence
            {
                Black = black.Select(b => new[] { b.Start, b.End }).ToArray(),
                Silence = silence.Select(s => new[] { s.Start, s.End }).ToArray(),
            };
            File.WriteAllText(cachePath, System.Text.Json.JsonSerializer.Serialize(toSave));
        }

        Console.WriteLine("Scoring thumbnails against reference...");
        var scores = Analyzer.ScoreThumbnails(thumbsDir, config.ReferenceImagePath, interval);

        var (segments, drop) = Analyzer.FindAdaptiveThresholdForTarget(
            scores, interval, expectedMinutes * 60.0, blackIntervals: black, silenceIntervals: silence);
        Console.WriteLine($"Settled on drop threshold {drop:F4}");

        var bridged = Analyzer.FindBlackBridgedBreaks(black);
        segments = Analyzer.MergeBlackBridgedBreaks(segments, bridged);
        Console.WriteLine($"Black-bridged {bridged.Count} additional candidate(s).");
        Console.WriteLine();

        var groundTruth = Eval.ParseKeptIntervalsFromTsv(tsvPath);
        var result = Eval.Score(segments, groundTruth, duration);
        Eval.PrintReport(result, segments, groundTruth);

        return 0;
    }

    private class CachedBlackSilence
    {
        public double[][] Black { get; set; } = Array.Empty<double[]>();
        public double[][] Silence { get; set; } = Array.Empty<double[]>();
    }
}
