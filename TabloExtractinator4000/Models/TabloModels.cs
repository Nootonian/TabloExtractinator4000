namespace TabloExtractinator4000.Models;

// ---------------------------------------------------------------------------
// Models mapped directly from real API responses (sample-data/*.json)
// ---------------------------------------------------------------------------

// Shared interface for anything that can be selected and exported
public interface IRecording
{
    int    ObjectId        { get; }
    string Path            { get; }   // API path, used to build watch URL
    string DisplayTitle    { get; }   // Ready-to-show in DataGrid
    string RecordingType   { get; }   // "Episode" | "Movie" | "Sport"
    DateTimeOffset AiredAt { get; }
    int    DurationSeconds { get; }   // actual recorded duration
    int    ScheduledSeconds{ get; }   // scheduled duration
    long   SizeBytes       { get; }
    string State           { get; }   // "finished", "recording", etc.
    string NetworkName     { get; }
    int    ChannelMajor    { get; }
    int    ChannelMinor    { get; }
    int    Width           { get; }
    int    Height          { get; }
    string AudioFormat     { get; }   // "ac3", "aac", etc.
    bool   IsInterlaced    { get; }
    bool   Watched         { get; }
    string ContainerFormat { get; }
}

// ---------------------------------------------------------------------------
// Episode (series recording)
// Mapped from /recordings/series/episodes/{id}
// ---------------------------------------------------------------------------
public record EpisodeRecording(
    int    ObjectId,
    string Path,
    string SeriesPath,
    string SeriesTitle,
    string EpisodeTitle,
    string? Description,
    int?   SeasonNumber,
    int?   EpisodeNumber,
    DateTimeOffset OriginalAirDate,
    DateTimeOffset AiredAt,
    int    DurationSeconds,
    int    ScheduledSeconds,
    long   SizeBytes,
    string State,
    string NetworkName,
    int    ChannelMajor,
    int    ChannelMinor,
    string ContainerFormat,
    int    Width,
    int    Height,
    string AudioFormat,
    bool   IsInterlaced,
    bool   Watched
) : IRecording
{
    public string DisplayTitle =>
        SeasonNumber.HasValue && EpisodeNumber.HasValue
            ? $"{SeriesTitle} - S{SeasonNumber:00}E{EpisodeNumber:00} - {EpisodeTitle}"
            : $"{SeriesTitle} - {EpisodeTitle}";
    public string RecordingType => "Episode";
}

// ---------------------------------------------------------------------------
// Movie airing
// Mapped from /recordings/movies/airings/{id}   (NOT the /recordings/movies/{id} summary)
// ---------------------------------------------------------------------------
public record MovieRecording(
    int    ObjectId,
    string Path,
    string MoviePath,
    string Title,
    int?   ReleaseYear,
    string? FilmRating,
    DateTimeOffset AiredAt,
    int    DurationSeconds,
    int    ScheduledSeconds,
    long   SizeBytes,
    string State,
    string NetworkName,
    int    ChannelMajor,
    int    ChannelMinor,
    string ContainerFormat,
    int    Width,
    int    Height,
    string AudioFormat,
    bool   IsInterlaced,
    bool   Watched
) : IRecording
{
    public string DisplayTitle    => ReleaseYear.HasValue ? $"{Title} ({ReleaseYear})" : Title;
    public string RecordingType   => "Movie";
    public string SeriesTitle     => Title;  // for filename template
    public DateTimeOffset OriginalAirDate => new DateTimeOffset(ReleaseYear ?? 1900, 1, 1, 0, 0, 0, TimeSpan.Zero);
}

// ---------------------------------------------------------------------------
// Sport event airing (same structure as movie airing, different category)
// ---------------------------------------------------------------------------
public record SportRecording(
    int    ObjectId,
    string Path,
    string SportPath,
    string Title,
    string? LeagueTitle,
    DateTimeOffset AiredAt,
    int    DurationSeconds,
    int    ScheduledSeconds,
    long   SizeBytes,
    string State,
    string NetworkName,
    int    ChannelMajor,
    int    ChannelMinor,
    string ContainerFormat,
    int    Width,
    int    Height,
    string AudioFormat,
    bool   IsInterlaced,
    bool   Watched
) : IRecording
{
    public string DisplayTitle  => Title;
    public string RecordingType => "Sport";
}

// ---------------------------------------------------------------------------
// Storage info
// Mapped from /server/harddrives
// ---------------------------------------------------------------------------
public record StorageInfo(
    string Name,
    long   TotalBytes,
    long   UsedBytes,
    long   FreeBytes,
    bool   Connected
);

// ---------------------------------------------------------------------------
// Device info
// Mapped from /server/info
// ---------------------------------------------------------------------------
public record DeviceInfo(
    string ServerId,
    string Name,
    string Version,
    string ModelName,
    int    Tuners,
    string LocalAddress
);

// ---------------------------------------------------------------------------
// Export job — one per IRecording selected for download
// ---------------------------------------------------------------------------
public enum ExportState
{
    Queued,         // tile created, not yet submitted to the extraction queue
    Pending,        // submitted, waiting for a free worker slot
    Discovering,    // fetching stream/playlist URL
    Downloading,
    Verifying,
    Verified,
    Failed,
    Cancelled,
    DeletedFromTablo
}

public class ExportJob
{
    public IRecording  Recording  { get; }
    public string      OutputPath { get; }

    // Per-job cancellation — lets the UI cancel/abort a single tile without
    // touching the others in the same batch.
    public CancellationTokenSource Cts { get; } = new();

    // True once the job has been dequeued from the parallelism semaphore and
    // started running (as opposed to still waiting in the "Pending" queue).
    public bool HasStarted { get; set; }

    // Per-job delete toggle — read live at the delete step, so the user can
    // flip it at any point up until extraction finishes, even mid-download.
    public bool DeleteAfterExtraction { get; set; }

    public ExportJob(IRecording recording, string outputPath)
    {
        Recording  = recording;
        OutputPath = outputPath;
    }

    public ExportState State               { get; set; } = ExportState.Queued;
    public double      ProgressPct         { get; set; }
    public string?     ErrorMessage        { get; set; }
    public bool        FfmpegSuccess       { get; set; }
    public bool        ProbeVerified       { get; set; }
    public long        OutputBytes         { get; set; }
    public int         OutputDurationSeconds { get; set; }
    public string?     PlaylistUrl         { get; set; }
    // Live download stats — updated each progress tick from ffmpeg
    public long        DownloadedBytes     { get; set; }
    public double      DownloadRateMBps    { get; set; }
    // Set when ffmpeg download begins — used to compute time-remaining estimate
    public DateTime    DownloadStartedAt   { get; set; } = DateTime.UtcNow;
}
