using System;

namespace CommercialCutter;

// One removed break, described in terms of where its splice point lands in the final
// (cut) output, so it can be cross-referenced against the original source recording.
public class CutMapRow
{
    public double OutputPositionSeconds { get; }
    public double SourceStartSeconds { get; }
    public double SourceEndSeconds { get; }

    public string OutputPosition => TimeSpan.FromSeconds(OutputPositionSeconds).ToString(@"hh\:mm\:ss");
    public string RemovedRange =>
        $"{TimeSpan.FromSeconds(SourceStartSeconds):hh\\:mm\\:ss}-{TimeSpan.FromSeconds(SourceEndSeconds):hh\\:mm\\:ss}";
    public string RemovedDuration => TimeSpan.FromSeconds(SourceEndSeconds - SourceStartSeconds).ToString(@"hh\:mm\:ss");

    public CutMapRow(double outputPositionSeconds, double sourceStartSeconds, double sourceEndSeconds)
    {
        OutputPositionSeconds = outputPositionSeconds;
        SourceStartSeconds = sourceStartSeconds;
        SourceEndSeconds = sourceEndSeconds;
    }
}
