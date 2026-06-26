using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace CommercialCutter;

// Keeps all generated working files (thumbnails, intermediate cut parts, settings) under the
// user's local app-data folder instead of next to the source recordings — those folders are
// often network shares or DVR-managed storage where the app shouldn't be leaving its own files.
public static class LocalPaths
{
    public static string RootDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CommercialCutter");

    public static string WorkRootDir => Path.Combine(RootDir, "work");

    public static string SettingsPath => Path.Combine(RootDir, "settings.json");

    // One stable folder per source file, keyed by its full path so two files with the same
    // name in different folders don't collide.
    public static string GetWorkDir(string videoPath)
    {
        var full = Path.GetFullPath(videoPath);
        var name = SanitizeFileName(Path.GetFileNameWithoutExtension(full));
        var dir = Path.Combine(WorkRootDir, $"{name}_{StableHash(full)}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string StableHash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes)[..8];
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }
}
