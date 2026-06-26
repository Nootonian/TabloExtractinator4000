namespace CommercialCutter;

// Pixel-space crop rectangle for the logo bug, relative to the source video's frame size.
public record CropRect(int X, int Y, int Width, int Height);

// Output of `selectbox`: where the logo lives and what it looks like when present.
public record CutterConfig(
    CropRect Crop,
    string   ReferenceImagePath,
    int      FrameWidth,
    int      FrameHeight);

// One sample taken during `catalog`/scored during `analyze`. Classification against a
// threshold happens later (see Analyzer.BuildSegments), so this just carries the raw score.
public record SampleScore(double TimeSeconds, double Similarity);

public record Segment(double StartSeconds, double EndSeconds, bool IsCommercial)
{
    public double DurationSeconds => EndSeconds - StartSeconds;
}
