using System.IO;
using System.Text.Json;

namespace CommercialCutter;

public class AppSettings
{
    public double IntervalSeconds { get; set; } = 1.0;
    public double AdUnitSeconds { get; set; } = 15.0;
    public double MinBreakSeconds { get; set; } = 59.0;
    public bool RefineTransitions { get; set; } = true;
    public double MaxNudgeSeconds { get; set; } = 6.0;
    public string CutMode { get; set; } = "Exact"; // "Fast", "Hybrid", or "Exact"
    public double HybridToleranceSeconds { get; set; } = 3.0;
    public string DetectionMode { get; set; } = "Adaptive"; // "Adaptive" or "Absolute"
    public double? ExpectedLengthMinutes { get; set; }
    public double? ManualThreshold { get; set; }
    public double? ManualDip { get; set; }
    public bool TrimEdgePromos { get; set; } = false;
    public double MaxPromoSeconds { get; set; } = 90.0;
    public string? LastVideoFolder { get; set; }
    public string? LastBatchFolder { get; set; }
}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(LocalPaths.SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(LocalPaths.SettingsPath), JsonOpts)
                       ?? new AppSettings();
        }
        catch
        {
            // Corrupt or unreadable settings file — fall back to defaults rather than crash on launch.
        }
        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(LocalPaths.RootDir);
        File.WriteAllText(LocalPaths.SettingsPath, JsonSerializer.Serialize(settings, JsonOpts));
    }
}
