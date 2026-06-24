using CommunityToolkit.Mvvm.ComponentModel;
using TabloExtractinator4000.Models;

namespace TabloExtractinator4000.ViewModels;

public partial class RecordingRowViewModel : ObservableObject
{
    public IRecording Recording { get; }

    [ObservableProperty] private bool _isSelected;

    // Raised on every IsSelected change, regardless of source (user click, Select
    // All, group checkbox). Property-level rather than a routed UI event so it
    // still fires for rows that aren't currently realized by DataGrid virtualization.
    public event Action<RecordingRowViewModel, bool>? SelectionChanged;

    partial void OnIsSelectedChanged(bool value) => SelectionChanged?.Invoke(this, value);

    public RecordingRowViewModel(IRecording recording)
    {
        Recording = recording;
    }

    // Group key — DataGrid groups rows by this value
    public string SeriesTitle => Recording switch
    {
        EpisodeRecording ep => ep.SeriesTitle,
        MovieRecording   mv => mv.Title,
        SportRecording   sp => sp.Title,
        _                   => Recording.DisplayTitle
    };

    // Episode column — episode title for episodes, empty for movies/sports
    public string EpisodeTitle => Recording is EpisodeRecording e ? e.EpisodeTitle : "";

    public string SeasonEpisode => Recording is EpisodeRecording ep
        ? (ep.SeasonNumber.HasValue && ep.EpisodeNumber.HasValue
            ? $"S{ep.SeasonNumber:00}E{ep.EpisodeNumber:00}"
            : ep.EpisodeNumber.HasValue ? $"E{ep.EpisodeNumber:00}" : "")
        : "";

    public string RecordingType  => Recording.RecordingType;
    public string AiredAt        => Recording.AiredAt.LocalDateTime.ToString("yyyy-MM-dd");
    public string Duration       => FormatDuration(Recording.DurationSeconds);
    public string SizeMb         => $"{Recording.SizeBytes / 1_048_576.0:F0} MB";
    public string NetworkName    => Recording.NetworkName;
    public string Channel        => $"{Recording.ChannelMajor}.{Recording.ChannelMinor}";
    public string State          => Recording.State;

    public string Tooltip
    {
        get
        {
            var parts = new List<string> { SeriesTitle };
            if (EpisodeTitle.Length > 0) parts.Add(EpisodeTitle);
            if (SeasonEpisode.Length > 0) parts.Add(SeasonEpisode);
            parts.Add($"{Recording.AiredAt.LocalDateTime:ddd MMM d yyyy  h:mm tt}  ·  {Duration}  ·  {SizeMb}");
            parts.Add($"Ch {Channel}  {NetworkName}  ·  {State}");
            if (Recording is EpisodeRecording ep2 && !string.IsNullOrEmpty(ep2.Description))
                parts.Add(ep2.Description);
            return string.Join("\n", parts);
        }
    }

    private static string FormatDuration(int seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}h {ts.Minutes:00}m"
            : $"{ts.Minutes}m";
    }
}
