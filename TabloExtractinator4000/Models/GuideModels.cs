namespace TabloExtractinator4000.Models;

public record GuideChannel(
    string Path,
    int    Major,
    int    Minor,
    string CallSign,
    string Network
)
{
    public string Label => Minor > 0 ? $"{Major}.{Minor} {CallSign}" : $"{Major} {CallSign}";
}

public record GuideAiring(
    string        Path,
    string        ChannelPath,
    DateTimeOffset StartsAt,
    int           DurationSeconds,
    string        ShowTitle,
    string        EpisodeTitle,
    string        Description,
    string        Type,           // "series" | "movie" | "sport"
    bool          IsScheduled
)
{
    public DateTimeOffset EndsAt => StartsAt.AddSeconds(DurationSeconds);
}
