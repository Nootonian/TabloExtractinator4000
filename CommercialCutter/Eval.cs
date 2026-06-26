using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace CommercialCutter;

// Scores a detected segment list against a manually-cut ground-truth TSV (Start/End/Name
// columns, as exported by LosslessCut), so detection quality can be measured and iterated on
// directly — no GUI run, no re-running ffmpeg — as long as the thumbnails and black/silence
// data are already cached on disk from a previous analysis.
public static class Eval
{
    // Ground truth rows are the segments to KEEP (program); gaps between them are commercials.
    public static List<(double Start, double End)> ParseKeptIntervalsFromTsv(string tsvPath)
    {
        var lines = File.ReadAllLines(tsvPath);
        var kept = new List<(double Start, double End)>();

        foreach (var line in lines.Skip(1)) // header: Start\tEnd\tName
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = line.Split('\t');
            if (parts.Length < 2) continue;
            if (!TryParseTimecode(parts[0], out var start) || !TryParseTimecode(parts[1], out var end)) continue;
            kept.Add((start, end));
        }

        return kept;
    }

    private static bool TryParseTimecode(string text, out double seconds)
    {
        // hh:mm:ss.fff
        seconds = 0;
        var m = System.Text.RegularExpressions.Regex.Match(text.Trim(), @"^(\d+):(\d+):(\d+(?:\.\d+)?)$");
        if (!m.Success) return false;
        seconds = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 3600
                + double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) * 60
                + double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        return true;
    }

    // Parses a free-form "cut <start> to <end>" per-line ground-truth list — the format used
    // when hand-marking commercial intervals directly (rather than exporting program/keep
    // intervals from an editor like LosslessCut). "end" means the end of the recording; a bare
    // "00" (no colons) means the very start.
    public static List<(double Start, double End)> ParseCutIntervalsFromTxt(string txtPath, double recordingDurationSeconds)
    {
        var lines = File.ReadAllLines(txtPath);
        var cuts = new List<(double Start, double End)>();

        foreach (var line in lines)
        {
            var m = System.Text.RegularExpressions.Regex.Match(line, @"cut\s+(?:from\s+)?(\S+)\s+to\s+(\S+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!m.Success) continue;
            var start = ParseTimeOrKeyword(m.Groups[1].Value, recordingDurationSeconds);
            var end = ParseTimeOrKeyword(m.Groups[2].Value, recordingDurationSeconds);
            cuts.Add((start, end));
        }

        return cuts;
    }

    private static double ParseTimeOrKeyword(string text, double recordingDurationSeconds)
    {
        text = text.Trim();
        if (text.Equals("end", StringComparison.OrdinalIgnoreCase)) return recordingDurationSeconds;
        if (TryParseTimecode(text, out var seconds)) return seconds;
        return double.TryParse(text, CultureInfo.InvariantCulture, out var raw) ? raw : 0;
    }

    // Inverts a list of commercial (cut) intervals into the kept/program intervals Score()
    // expects, by taking the gaps between them across [0, recordingDurationSeconds].
    public static List<(double Start, double End)> InvertCutsToKeptIntervals(
        List<(double Start, double End)> cuts, double recordingDurationSeconds)
    {
        var sorted = cuts.OrderBy(c => c.Start).ToList();
        var kept = new List<(double Start, double End)>();
        var cursor = 0.0;

        foreach (var cut in sorted)
        {
            if (cut.Start > cursor) kept.Add((cursor, cut.Start));
            cursor = Math.Max(cursor, cut.End);
        }
        if (cursor < recordingDurationSeconds) kept.Add((cursor, recordingDurationSeconds));

        return kept;
    }

    public class ScoreResult
    {
        public double MisclassifiedSeconds;
        public double FalsePositiveSeconds; // we removed something that should've been kept
        public double FalseNegativeSeconds; // we kept something that should've been removed
        public double TotalDurationSeconds;
        public List<string> Notes = new();
    }

    // Walks both interval lists on a shared timeline and sums up disagreement. This is far more
    // informative than comparing total kept-duration alone, since two very differently-wrong
    // segmentations can happen to sum to the same total.
    public static ScoreResult Score(List<Segment> detected, List<(double Start, double End)> groundTruthKept, double totalDuration)
    {
        var result = new ScoreResult { TotalDurationSeconds = totalDuration };

        bool IsKeptByGroundTruth(double t) => groundTruthKept.Any(k => t >= k.Start && t < k.End);
        bool IsKeptByDetection(double t) => detected.Any(s => t >= s.StartSeconds && t < s.EndSeconds && !s.IsCommercial);

        const double step = 0.5;
        for (double t = 0; t < totalDuration; t += step)
        {
            var gtKept = IsKeptByGroundTruth(t);
            var detKept = IsKeptByDetection(t);
            if (gtKept == detKept) continue;

            result.MisclassifiedSeconds += step;
            if (detKept && !gtKept) result.FalsePositiveSeconds += step; // kept junk
            if (!detKept && gtKept) result.FalseNegativeSeconds += step; // cut real program
        }

        return result;
    }

    public static void PrintReport(ScoreResult r, List<Segment> detected, List<(double Start, double End)> groundTruthKept)
    {
        Console.WriteLine($"Total duration:        {TimeSpan.FromSeconds(r.TotalDurationSeconds):hh\\:mm\\:ss}");
        Console.WriteLine($"Misclassified:         {TimeSpan.FromSeconds(r.MisclassifiedSeconds):hh\\:mm\\:ss} ({r.MisclassifiedSeconds / r.TotalDurationSeconds * 100:F2}%)");
        Console.WriteLine($"  Kept junk (FP):      {TimeSpan.FromSeconds(r.FalsePositiveSeconds):hh\\:mm\\:ss}");
        Console.WriteLine($"  Cut real program (FN): {TimeSpan.FromSeconds(r.FalseNegativeSeconds):hh\\:mm\\:ss}");
        Console.WriteLine();
        Console.WriteLine("Ground truth gaps (commercials to remove):");
        for (int i = 0; i < groundTruthKept.Count - 1; i++)
            Console.WriteLine($"  {TimeSpan.FromSeconds(groundTruthKept[i].End):hh\\:mm\\:ss} - {TimeSpan.FromSeconds(groundTruthKept[i + 1].Start):hh\\:mm\\:ss}");
        Console.WriteLine();
        Console.WriteLine("Detected segments:");
        foreach (var s in detected)
            Console.WriteLine($"  [{(s.IsCommercial ? "AD " : "PRG")}] {TimeSpan.FromSeconds(s.StartSeconds):hh\\:mm\\:ss} - {TimeSpan.FromSeconds(s.EndSeconds):hh\\:mm\\:ss}");
    }
}
