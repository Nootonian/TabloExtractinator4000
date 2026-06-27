using System.Globalization;
using System.Text.Json;
using CommercialCutter;

if (args.Length < 6)
{
    Console.Error.WriteLine("usage: evaltool <video> <config.json> <thumbsDir> <groundTruthTsv> <intervalSeconds> <expectedMinutes>");
    return 1;
}

var video = args[0];
var config = ConfigStore.LoadConfig(args[1]);
var thumbsDir = args[2];
var tsvPath = args[3];
var interval = double.Parse(args[4], CultureInfo.InvariantCulture);
var expectedMinutes = double.Parse(args[5], CultureInfo.InvariantCulture);

var duration = await Ffmpeg.GetDurationSecondsAsync(video);

var cachePath = video + ".blacksilence.cache.json";
List<(double Start, double End)> black, silence;
if (File.Exists(cachePath))
{
    Console.WriteLine("Loading cached black/silence intervals...");
    var cached = JsonSerializer.Deserialize<CachedBlackSilence>(File.ReadAllText(cachePath))!;
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
    File.WriteAllText(cachePath, JsonSerializer.Serialize(toSave));
}

Console.WriteLine("Scoring thumbnails against reference...");
var scores = Analyzer.ScoreThumbnails(thumbsDir, config.ReferenceImagePath, interval);

List<Segment> segments;
double drop;
var fixedDropArgIndex = Array.IndexOf(args, "--fixed-drop");
var adUnitArgIndex = Array.IndexOf(args, "--ad-unit");
var adUnitSeconds = adUnitArgIndex >= 0 ? double.Parse(args[adUnitArgIndex + 1], CultureInfo.InvariantCulture) : 15.0;
var minBreakArgIndex = Array.IndexOf(args, "--min-break");
var minBreakSeconds = minBreakArgIndex >= 0 ? double.Parse(args[minBreakArgIndex + 1], CultureInfo.InvariantCulture) : 59.0;

if (fixedDropArgIndex >= 0)
{
    drop = double.Parse(args[fixedDropArgIndex + 1], CultureInfo.InvariantCulture);
    var baseline = Analyzer.ComputeLocalBaseline(scores, interval, Analyzer.LocalWindowSeconds);
    segments = Analyzer.BuildSegmentsAdaptiveFromBaseline(scores, interval, drop, baseline, adUnitSeconds, minBreakSeconds);
    Console.WriteLine($"Before corroboration: {segments.Count(s => s.IsCommercial)} commercial segment(s)");
    foreach (var s in segments.Where(s => s.IsCommercial))
        Console.WriteLine($"  raw AD {TimeSpan.FromSeconds(s.StartSeconds):hh\\:mm\\:ss} - {TimeSpan.FromSeconds(s.EndSeconds):hh\\:mm\\:ss}");
    segments = Analyzer.ValidateBreaksAgainstBumpers(segments, black, silence);
    segments = Analyzer.RefineBoundariesWithBlackAndSilence(segments, black, silence);
    Console.WriteLine($"Using fixed drop threshold {drop:F4}");
}
else
{
    (segments, drop) = Analyzer.FindAdaptiveThresholdForTarget(
        scores, interval, expectedMinutes * 60.0, blackIntervals: black, silenceIntervals: silence);
    Console.WriteLine($"Settled on drop threshold {drop:F4}");
}

if (args.Contains("--dump-grid"))
{
    var baseline = Analyzer.ComputeLocalBaseline(scores, interval, Analyzer.LocalWindowSeconds);
    var bridged = Analyzer.FindBlackBridgedBreaks(black, scores, baseline);
    var target = expectedMinutes * 60.0;
    for (int i = 0; i <= 200; i++)
    {
        double candidate = 0.6 * i / 200;
        var segs = Analyzer.BuildSegmentsAdaptiveFromBaseline(scores, interval, candidate, baseline);
        segs = Analyzer.ValidateBreaksAgainstBumpers(segs, black, silence);
        segs = Analyzer.RefineBoundariesWithBlackAndSilence(segs, black, silence);
        segs = Analyzer.MergeBlackBridgedBreaks(segs, bridged);
        double programTotal = segs.Where(s => !s.IsCommercial).Sum(s => s.DurationSeconds);
        Console.WriteLine($"{candidate:F4}\t{programTotal:F1}\t{Math.Abs(programTotal - target):F1}\t{segs.Count(s => s.IsCommercial)}");
    }
    return 0;
}

if (args.Contains("--bridge"))
{
    var minAbsentArgIndex = Array.IndexOf(args, "--min-absent");
    var minAbsentFraction = minAbsentArgIndex >= 0 ? double.Parse(args[minAbsentArgIndex + 1], CultureInfo.InvariantCulture) : 0.40;
    var bridgeBaseline = Analyzer.ComputeLocalBaseline(scores, interval, Analyzer.LocalWindowSeconds);
    var bridged = Analyzer.FindBlackBridgedBreaks(black, scores, bridgeBaseline, minAbsentFraction: minAbsentFraction);
    Console.WriteLine($"Black-bridged candidates: {bridged.Count}");
    foreach (var b in bridged)
        Console.WriteLine($"  bridge {TimeSpan.FromSeconds(b.Start):hh\\:mm\\:ss} - {TimeSpan.FromSeconds(b.End):hh\\:mm\\:ss}");
    segments = Analyzer.MergeBlackBridgedBreaks(segments, bridged);
}

if (args.Contains("--trim-promo"))
{
    var beforeCount = segments.Count(s => s.IsCommercial);
    segments = Analyzer.TrimLeadingTrailingPromo(segments, black, silence, 90.0, duration);
    Console.WriteLine($"Trim-promo: {segments.Count(s => s.IsCommercial) - beforeCount} segment(s) added/changed");
}
Console.WriteLine();

var isCutFormat = File.ReadAllLines(tsvPath).Any(l => System.Text.RegularExpressions.Regex.IsMatch(l, @"cut\s+(?:from\s+)?\S+\s+to\s+\S+", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
var groundTruth = isCutFormat
    ? Eval.InvertCutsToKeptIntervals(Eval.ParseCutIntervalsFromTxt(tsvPath, duration), duration)
    : Eval.ParseKeptIntervalsFromTsv(tsvPath);
var result = Eval.Score(segments, groundTruth, duration);
Eval.PrintReport(result, segments, groundTruth);

var inspectArgIndex = Array.IndexOf(args, "--inspect");
if (inspectArgIndex >= 0)
{
    var rangeStart = double.Parse(args[inspectArgIndex + 1], CultureInfo.InvariantCulture);
    var rangeEnd = double.Parse(args[inspectArgIndex + 2], CultureInfo.InvariantCulture);
    var baseline = Analyzer.ComputeLocalBaseline(scores, interval, Analyzer.LocalWindowSeconds);
    Console.WriteLine();
    Console.WriteLine($"--- Inspecting {rangeStart}-{rangeEnd}s ---");
    EvalTool.Inspect.DumpRange(scores, baseline, drop, rangeStart, rangeEnd);
}

return 0;

class CachedBlackSilence
{
    public double[][] Black { get; set; } = Array.Empty<double[]>();
    public double[][] Silence { get; set; } = Array.Empty<double[]>();
}
