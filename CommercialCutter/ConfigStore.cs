using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace CommercialCutter;

public static class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static void SaveConfig(string path, CutterConfig config) =>
        File.WriteAllText(path, JsonSerializer.Serialize(config, JsonOpts));

    public static CutterConfig LoadConfig(string path) =>
        JsonSerializer.Deserialize<CutterConfig>(File.ReadAllText(path), JsonOpts)
        ?? throw new InvalidOperationException($"Could not parse config: {path}");

    public static void SaveScores(string path, List<SampleScore> scores) =>
        File.WriteAllText(path, JsonSerializer.Serialize(scores, JsonOpts));

    public static List<SampleScore> LoadScores(string path) =>
        JsonSerializer.Deserialize<List<SampleScore>>(File.ReadAllText(path), JsonOpts)
        ?? throw new InvalidOperationException($"Could not parse scores: {path}");

    public static void SaveSegments(string path, List<Segment> segments) =>
        File.WriteAllText(path, JsonSerializer.Serialize(segments, JsonOpts));

    public static List<Segment> LoadSegments(string path) =>
        JsonSerializer.Deserialize<List<Segment>>(File.ReadAllText(path), JsonOpts)
        ?? throw new InvalidOperationException($"Could not parse segments: {path}");
}
