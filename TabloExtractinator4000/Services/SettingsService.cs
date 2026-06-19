using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TabloExtractinator4000.Services;

public class AppSettings
{
    // In-memory plaintext password. Written to JSON only during migration (as empty string);
    // real storage is PasswordEncrypted. On Load it is populated from decryption.
    public string Password           { get; set; } = "";

    // DPAPI-encrypted password stored as Base64.
    public string PasswordEncrypted  { get; set; } = "";

    public string Email              { get; set; } = "";
    public string OutputFolder       { get; set; } = "";
    public string MovieOutputFolder  { get; set; } = "";
    public string EpisodeTemplate    { get; set; } = "{SeriesTitle} - S{Season:00}E{Episode:00} - {EpisodeTitle}";
    public string MovieTemplate      { get; set; } = "{Title} ({Year})";
    public string FfmpegExtraArgs    { get; set; } = "-c:v libx264 -preset fast -crf 23 -c:a aac";
    public int    MaxParallelDownloads { get; set; } = 1;
    public string VlcPath            { get; set; } = "";
}

public class SettingsService
{
    public string ConfigPath => System.IO.Path.Combine(AppContext.BaseDirectory, "tablo.config.json");

    private static readonly JsonSerializerOptions _opts = new()
    {
        WriteIndented        = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public AppSettings Load()
    {
        try
        {
            if (!System.IO.File.Exists(ConfigPath))
                return Defaults();

            var json     = System.IO.File.ReadAllText(ConfigPath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, _opts) ?? Defaults();
            BackfillDefaults(settings);

            if (!string.IsNullOrEmpty(settings.PasswordEncrypted))
            {
                // Normal path: decrypt stored ciphertext
                settings.Password = Decrypt(settings.PasswordEncrypted);
            }
            else if (!string.IsNullOrEmpty(settings.Password))
            {
                // Migration: old config stored plaintext in "password" field — encrypt and re-save
                Save(settings);
            }

            return settings;
        }
        catch { return Defaults(); }
    }

    public void Save(AppSettings settings)
    {
        // Encrypt the in-memory password, then clear plaintext before serializing
        settings.PasswordEncrypted = string.IsNullOrEmpty(settings.Password)
            ? ""
            : Encrypt(settings.Password);

        var plaintext = settings.Password;
        settings.Password = "";
        try
        {
            var json = JsonSerializer.Serialize(settings, _opts);
            System.IO.File.WriteAllText(ConfigPath, json);
        }
        finally
        {
            settings.Password = plaintext;  // restore in-memory value
        }
    }

    // DPAPI: tied to the current Windows user account — only this user on this machine
    // can decrypt the value, even if the config file is copied elsewhere.
    private static string Encrypt(string plaintext)
    {
        var bytes     = Encoding.UTF8.GetBytes(plaintext);
        var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encrypted);
    }

    private static string Decrypt(string base64)
    {
        try
        {
            var encrypted = Convert.FromBase64String(base64);
            var bytes     = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch { return ""; }
    }

    private static void BackfillDefaults(AppSettings s)
    {
        var def = Defaults();
        if (string.IsNullOrEmpty(s.OutputFolder))      s.OutputFolder      = def.OutputFolder;
        if (string.IsNullOrEmpty(s.MovieOutputFolder)) s.MovieOutputFolder = def.MovieOutputFolder;
        if (string.IsNullOrEmpty(s.EpisodeTemplate))   s.EpisodeTemplate   = def.EpisodeTemplate;
        if (string.IsNullOrEmpty(s.MovieTemplate))     s.MovieTemplate     = def.MovieTemplate;
        if (string.IsNullOrEmpty(s.FfmpegExtraArgs))   s.FfmpegExtraArgs   = def.FfmpegExtraArgs;
    }

    private static AppSettings Defaults()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        return new AppSettings
        {
            OutputFolder      = System.IO.Path.Combine(desktop, "TabloExports", "Episodes"),
            MovieOutputFolder = System.IO.Path.Combine(desktop, "TabloExports", "Movies"),
            VlcPath           = FindVlc(),
        };
    }

    private static string FindVlc()
    {
        string[] candidates =
        [
            @"C:\Program Files\VideoLAN\VLC\vlc.exe",
            @"C:\Program Files (x86)\VideoLAN\VLC\vlc.exe",
        ];
        return candidates.FirstOrDefault(System.IO.File.Exists) ?? "";
    }
}
