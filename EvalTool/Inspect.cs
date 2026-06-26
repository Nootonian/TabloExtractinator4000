using CommercialCutter;

namespace EvalTool;

public static class Inspect
{
    public static void DumpRange(List<SampleScore> scores, double[] baseline, double dropThreshold, double startSec, double endSec)
    {
        foreach (var s in scores)
        {
            if (s.TimeSeconds < startSec || s.TimeSeconds > endSec) continue;
            var idx = (int)s.TimeSeconds;
            var b = idx < baseline.Length ? baseline[idx] : double.NaN;
            var present = s.Similarity >= b - dropThreshold;
            Console.WriteLine($"t={s.TimeSeconds,6:F0}  sim={s.Similarity:F3}  baseline={b:F3}  cutoff={b - dropThreshold:F3}  present={present}");
        }
    }
}
