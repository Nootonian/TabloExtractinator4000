using TabloExtractinator4000.Models;

namespace TabloExtractinator4000.Services;

// ---------------------------------------------------------------------------
// Generates output filenames from recording metadata.
//
// Templates use {Token} placeholders. Defaults are Jellyfin/Plex compatible.
// ---------------------------------------------------------------------------
public class FilenameService
{
    // Defaults — user can override in settings
    public string EpisodeTemplate { get; set; } =
        "{SeriesTitle} - S{Season:00}E{Episode:00} - {EpisodeTitle}";

    public string MovieTemplate { get; set; } =
        "{Title} ({Year})";

    public string SportTemplate { get; set; } =
        "{Title} - {AirDate:yyyy-MM-dd}";

    // Extension is always .mp4 — ffmpeg remuxes MPEG-2 TS → MP4 container
    public string Extension { get; set; } = ".mp4";

    public string GenerateFilename(IRecording recording)
    {
        var showName = recording switch
        {
            EpisodeRecording ep => ep.SeriesTitle,
            MovieRecording   mv => mv.Title,
            SportRecording   sp => sp.Title,
            _                   => recording.DisplayTitle
        };

        var raw = recording switch
        {
            EpisodeRecording ep => ApplyEpisodeTemplate(ep),
            MovieRecording   mv => ApplyMovieTemplate(mv),
            SportRecording   sp => ApplySportTemplate(sp),
            _                   => recording.DisplayTitle
        };

        // Place each recording in a subfolder named after the show
        return System.IO.Path.Combine(Sanitize(showName), Sanitize(raw) + Extension);
    }

    private string ApplyEpisodeTemplate(EpisodeRecording ep)
    {
        var result = EpisodeTemplate
            .Replace("{SeriesTitle}",  ep.SeriesTitle)
            .Replace("{Season:00}",   (ep.SeasonNumber  ?? 1).ToString("00"))
            .Replace("{Episode:00}",  (ep.EpisodeNumber ?? 1).ToString("00"))
            .Replace("{EpisodeTitle}", ep.EpisodeTitle)
            .Replace("{AirDate:yyyy-MM-dd}", ep.AiredAt.ToString("yyyy-MM-dd"))
            .Replace("{Network}",     ep.NetworkName);

        // Trim trailing separators left behind by empty tokens (e.g. " - " when EpisodeTitle is "")
        return System.Text.RegularExpressions.Regex.Replace(result.Trim(), @"[\s\-]+$", "").Trim();
    }

    private string ApplyMovieTemplate(MovieRecording mv)
    {
        return MovieTemplate
            .Replace("{Title}", mv.Title)
            .Replace("{Year}",  mv.ReleaseYear?.ToString() ?? "")
            .Replace("{Rating}", mv.FilmRating ?? "")
            .Replace("{AirDate:yyyy-MM-dd}", mv.AiredAt.ToString("yyyy-MM-dd"))
            .Replace("{Network}", mv.NetworkName);
    }

    private string ApplySportTemplate(SportRecording sp)
    {
        return SportTemplate
            .Replace("{Title}",   sp.Title)
            .Replace("{League}",  sp.LeagueTitle ?? "")
            .Replace("{AirDate:yyyy-MM-dd}", sp.AiredAt.ToString("yyyy-MM-dd"))
            .Replace("{Network}", sp.NetworkName);
    }

    // Remove characters that are illegal in Windows filenames
    private static string Sanitize(string name)
    {
        // Colon is the most common "invalid" character in real titles (e.g. "9-1-1: Nashville") —
        // give it a readable separator instead of an underscore.
        name = name.Replace(":", " -");

        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder();
        foreach (var c in name)
            if (!invalid.Contains(c)) sb.Append(c);  // drop illegal chars rather than substituting

        return System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\s{2,}", " ").Trim();
    }
}
