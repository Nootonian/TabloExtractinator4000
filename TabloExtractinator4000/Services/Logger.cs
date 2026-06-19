using System.IO;

namespace TabloExtractinator4000.Services;

public static class Logger
{
    public static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TabloExtractinator4000", "debug.log");

    static Logger()
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!); } catch { }
    }

    public static void Log(string message)
    {
        try { File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}\r\n"); }
        catch { }
    }

    public static void Clear()
    {
        try { File.WriteAllText(LogPath, ""); } catch { }
    }
}
