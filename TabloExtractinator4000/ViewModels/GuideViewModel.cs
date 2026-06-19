using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Application = System.Windows.Application;
using CommunityToolkit.Mvvm.Input;
using TabloExtractinator4000.Models;
using TabloExtractinator4000.Services;

namespace TabloExtractinator4000.ViewModels;

// One cell in the horizontal Canvas strip for a channel row.
// Extends ObservableObject so IsScheduled changes propagate to WPF bindings.
public partial class GuideAiringCell : ObservableObject
{
    public GuideAiring Airing { get; init; } = null!;
    public double      X      { get; init; }
    public double      Width  { get; init; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Background))]
    [NotifyPropertyChangedFor(nameof(BorderColor))]
    [NotifyPropertyChangedFor(nameof(Tooltip))]
    private bool _isScheduled;

    public bool IsCurrentlyAiring => Airing.StartsAt <= DateTimeOffset.Now && DateTimeOffset.Now < Airing.EndsAt;
    public bool IsFuture          => Airing.StartsAt > DateTimeOffset.Now;

    public string Label   => Airing.ShowTitle + (Airing.EpisodeTitle.Length > 0 ? $"  –  {Airing.EpisodeTitle}" : "");
    public string Tooltip => BuildTooltip();

    public string Background => IsScheduled       ? "#1D4A2A" :
                                IsCurrentlyAiring ? "#1A3A5C" :
                                Airing.Type == "movie" ? "#2D2A4A" :
                                Airing.Type == "sport" ? "#2A3D4A" : "#252526";
    public string BorderColor => IsScheduled       ? "#2EA043" :
                                 IsCurrentlyAiring ? "#4FC3F7" : "#3F3F46";

    private string BuildTooltip()
    {
        var local = Airing.StartsAt.LocalDateTime;
        var end   = Airing.EndsAt.LocalDateTime;
        var dur   = TimeSpan.FromSeconds(Airing.DurationSeconds);
        var parts = new List<string> { Airing.ShowTitle };
        if (Airing.EpisodeTitle.Length > 0) parts.Add(Airing.EpisodeTitle);
        parts.Add($"{local:ddd M/d  h:mm tt} – {end:h:mm tt}  ({(int)dur.TotalMinutes} min)");
        if (Airing.Description.Length > 0)  parts.Add(Airing.Description);
        if (IsScheduled)        parts.Add("✓ Scheduled to record");
        else if (IsCurrentlyAiring) parts.Add("▶ On now — double-click to watch live");
        else if (IsFuture)          parts.Add("Double-click to schedule");
        return string.Join("\n", parts);
    }
}

// One row in the guide — a channel + its airing cells.
public class GuideChannelRow
{
    public GuideChannel                       Channel { get; init; } = null!;
    public ObservableCollection<GuideAiringCell> Cells { get; } = [];
}

public class TimeHeaderCell
{
    public string Label { get; init; } = "";
    public double X     { get; init; }
}

public class DayHeaderCell
{
    public string Label { get; init; } = "";
    public double X     { get; init; }
    public double Width { get; init; }
}

public partial class GuideViewModel : ObservableObject
{
    private readonly TabloApiService _api;
    private readonly SettingsService _settings;

    public const double PxPerMinute = 6.0;

    [ObservableProperty] private bool   _isLoading;
    [ObservableProperty] private string _statusText = "Click  ↺ Load Guide  to fetch the TV grid.";
    [ObservableProperty] private double _canvasWidth = 2880;

    public event Action<double>? ScrollToRequested;

    [ObservableProperty] private ObservableCollection<DayHeaderCell>  _dayHeaders  = [];
    [ObservableProperty] private ObservableCollection<TimeHeaderCell> _timeHeaders = [];
    public ObservableCollection<GuideChannelRow> Rows { get; } = [];

    private DateTimeOffset _gridStart;
    private readonly Dictionary<string, GuideChannelRow> _rowsByChannelPath = [];

    public GuideViewModel(TabloApiService api, SettingsService settings)
    {
        _api      = api;
        _settings = settings;
    }

    [RelayCommand]
    private async Task LoadGuideAsync(CancellationToken ct)
    {
        IsLoading  = true;
        StatusText = "Loading channels…";
        Services.Logger.Log("=== LoadGuide START ===");
        Rows.Clear();
        DayHeaders.Clear();
        TimeHeaders.Clear();
        _rowsByChannelPath.Clear();
        _gridStart = DateTimeOffset.MinValue;

        try
        {
            var channels = await _api.GetGuideChannelsAsync(ct);
            if (channels.Count == 0) { StatusText = "No channels found."; return; }

            // Snap to the previous 30-minute mark — no earlier.
            // This puts "now" at canvas X ≈ 180px (30 min * 6px), and scroll-to-now
            // with -200 offset clamps to 0, so the viewport starts at the canvas left.
            // Clipped cells (started before _gridStart) at X=0 are then immediately visible.
            var nowUtc     = DateTimeOffset.UtcNow;
            int snapMinute = nowUtc.Minute >= 30 ? 30 : 0;
            var snapUtc    = new DateTime(nowUtc.Year, nowUtc.Month, nowUtc.Day,
                nowUtc.Hour, snapMinute, 0, DateTimeKind.Utc);
            _gridStart  = new DateTimeOffset(snapUtc);
            CanvasWidth = 48 * 60 * PxPerMinute;

            BuildTimeHeaders(_gridStart, _gridStart.AddHours(48));

            foreach (var ch in channels)
            {
                var row = new GuideChannelRow { Channel = ch };
                Rows.Add(row);
                _rowsByChannelPath[ch.Path] = row;
            }

            StatusText = $"{channels.Count} channels — loading airings…";

            // Scroll to "now" immediately so the user doesn't see empty midnight content
            ScrollToNowInternal();

            // Phase 2: stream airings in, adding cells to rows as they arrive
            int totalAirings = 0;
            var gridEnd = _gridStart.AddHours(48);
            var progress = new Progress<(int done, int total)>(p =>
                StatusText = $"Fetching guide data ({p.done} / {p.total})…");

            await _api.StreamGuideAiringsAsync(OnAiringBatch, progress, ct);

            Services.Logger.Log($"Guide load complete: {channels.Count} channels, {totalAirings} airings displayed (48h window)");
            StatusText = $"Guide loaded — {channels.Count} channels, {totalAirings} airings (48h window)";
            ScrollToNowInternal();   // re-snap in case scrollbar shifted during load

            void OnAiringBatch(IEnumerable<GuideAiring> batch)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    foreach (var a in batch)
                    {
                        // Skip airings entirely outside our 48-hour window
                        if (a.EndsAt <= _gridStart || a.StartsAt >= gridEnd) continue;
                        if (!_rowsByChannelPath.TryGetValue(a.ChannelPath, out var row)) continue;

                        totalAirings++;

                        // X is relative to fixed _gridStart; clip programs that started before it
                        var rawX = (a.StartsAt - _gridStart).TotalMinutes * PxPerMinute;
                        var x    = Math.Max(rawX, 0);
                        var w    = Math.Max(a.DurationSeconds / 60.0 * PxPerMinute - (x - rawX) - 2, 4);
                        row.Cells.Add(new GuideAiringCell { Airing = a, X = x, Width = w, IsScheduled = a.IsScheduled });
                    }
                });
            }
        }
        catch (OperationCanceledException) { StatusText = "Cancelled."; }
        catch (Exception ex)              { StatusText = $"Error: {ex.Message}"; }
        finally                           { IsLoading = false; }
    }

    private void BuildTimeHeaders(DateTimeOffset start, DateTimeOffset end)
    {
        // Time labels — every 30 minutes
        var times = new ObservableCollection<TimeHeaderCell>();
        for (var t = start; t < end; t = t.AddMinutes(30))
        {
            times.Add(new TimeHeaderCell
            {
                Label = t.LocalDateTime.ToString("h:mm tt"),
                X     = (t - start).TotalMinutes * PxPerMinute
            });
        }
        TimeHeaders = times;

        // Day labels — one per calendar day in local time
        var days = new ObservableCollection<DayHeaderCell>();
        var t2   = start;
        while (t2 < end)
        {
            var localDate  = t2.LocalDateTime.Date;
            var nextLocalDay = new DateTimeOffset(localDate.AddDays(1),
                TimeZoneInfo.Local.GetUtcOffset(localDate.AddDays(1)));
            var dayEnd = nextLocalDay < end ? nextLocalDay : end;

            days.Add(new DayHeaderCell
            {
                Label = localDate.ToString("dddd, MMMM d"),
                X     = (t2 - start).TotalMinutes * PxPerMinute,
                Width = (dayEnd - t2).TotalMinutes * PxPerMinute
            });
            t2 = dayEnd;
        }
        DayHeaders = days;
    }

    [RelayCommand]
    private void ScrollToNow() => ScrollToNowInternal();

    private void ScrollToNowInternal()
    {
        if (_gridStart == DateTimeOffset.MinValue) return;
        // Use UTC for consistent math — _gridStart is UTC midnight, Now.ToUniversalTime() matches
        var nowOffset = (DateTimeOffset.UtcNow - _gridStart).TotalMinutes * PxPerMinute;
        ScrollToRequested?.Invoke(Math.Max(0, nowOffset - 200));
    }

    // Called from code-behind on double-click of an airing cell
    public async Task AiringDoubleClickAsync(GuideAiringCell cell)
    {
        if (cell.IsCurrentlyAiring)
        {
            // Show info + option to watch live
            await ShowInfoDialogAsync(cell, allowPlay: true);
        }
        else
        {
            // Future OR past — show info/schedule dialog
            await ShowScheduleDialogAsync(cell);
        }
    }

    // Called from code-behind on double-click of a channel label
    public async Task ChannelDoubleClickAsync(GuideChannelRow row)
    {
        await PlayLiveByChannelPathAsync(row.Channel.Path);
    }

    [RelayCommand]
    private async Task PlayLiveChannelAsync(GuideChannelRow? row)
    {
        if (row == null) return;
        await PlayLiveByChannelPathAsync(row.Channel.Path);
    }

    [RelayCommand]
    private async Task PlayCurrentAiringAsync(GuideAiringCell? cell)
    {
        if (cell == null) return;
        await PlayLiveByChannelPathAsync(cell.Airing.ChannelPath);
    }

    private async Task PlayLiveByChannelPathAsync(string channelPath)
    {
        try
        {
            var player = _settings.Load().VlcPath;
            if (string.IsNullOrEmpty(player) || !System.IO.File.Exists(player))
            {
                StatusText = "Set player path in Settings first.";
                return;
            }
            StatusText = "Starting live stream…";
            var url = await _api.GetLiveStreamUrlAsync(channelPath);
            if (string.IsNullOrEmpty(url)) { StatusText = "No stream URL returned."; return; }
            var psi = new System.Diagnostics.ProcessStartInfo(player)
            {
                UseShellExecute  = false,
                WorkingDirectory = System.IO.Path.GetDirectoryName(player) ?? "",
            };
            psi.ArgumentList.Add(url);
            psi.ArgumentList.Add("--volume=25");
            System.Diagnostics.Process.Start(psi);
            StatusText = "Playing live.";
        }
        catch (Exception ex) { StatusText = $"Play failed: {ex.Message}"; }
    }

    private async Task ShowInfoDialogAsync(GuideAiringCell cell, bool allowPlay)
    {
        var dlg = new Views.GuideAiringDetailWindow(
            cell.Airing,
            isOnNow:          allowPlay,
            allowSchedule:    cell.IsFuture && !cell.IsScheduled,
            alreadyScheduled: cell.IsScheduled)
        { Owner = Application.Current.MainWindow };
        var result = dlg.ShowDialog();
        if      (result == true && dlg.UserWantsToSchedule)   await ScheduleAiringInternalAsync(cell);
        else if (result == true && dlg.UserWantsToPlay)        await PlayLiveByChannelPathAsync(cell.Airing.ChannelPath);
        else if (result == true && dlg.UserWantsToUnschedule) await UnscheduleRecordingAsync(cell);
    }

    private async Task ShowScheduleDialogAsync(GuideAiringCell cell)
    {
        var dlg = new Views.GuideAiringDetailWindow(
            cell.Airing,
            isOnNow:          false,
            allowSchedule:    cell.IsFuture && !cell.IsScheduled,
            alreadyScheduled: cell.IsScheduled)
        { Owner = Application.Current.MainWindow };
        var result = dlg.ShowDialog();
        if      (result == true && dlg.UserWantsToSchedule)   await ScheduleAiringInternalAsync(cell);
        else if (result == true && dlg.UserWantsToUnschedule) await UnscheduleRecordingAsync(cell);
    }

    [RelayCommand]
    private async Task ShowAiringInfoAsync(GuideAiringCell? cell)
    {
        if (cell == null) return;
        await ShowInfoDialogAsync(cell, allowPlay: cell.IsCurrentlyAiring);
    }

    [RelayCommand]
    private void ShowAiringDetails(GuideAiringCell? cell)
    {
        if (cell == null) return;
        var a     = cell.Airing;
        var local = a.StartsAt.LocalDateTime;
        var end   = a.EndsAt.LocalDateTime;
        var dur   = TimeSpan.FromSeconds(a.DurationSeconds);
        var parts = new List<string>();
        parts.Add(a.ShowTitle);
        if (a.EpisodeTitle.Length > 0) parts.Add($"\"{a.EpisodeTitle}\"");
        parts.Add("");
        parts.Add($"Airs:    {local:ddd MMM d, yyyy  h:mm tt} – {end:h:mm tt}  ({(int)dur.TotalMinutes} min)");
        parts.Add($"Type:    {System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(a.Type)}");
        if (cell.IsScheduled)            parts.Add("Status:  ✓ Scheduled to record");
        else if (cell.IsCurrentlyAiring) parts.Add("Status:  ▶ On now");
        else if (cell.IsFuture)          parts.Add("Status:  Future");
        else                             parts.Add("Status:  Past");
        if (a.Description.Length > 0) { parts.Add(""); parts.Add(a.Description); }
        var dlg = new Views.DetailsWindow(a.ShowTitle, string.Join("\n", parts))
            { Owner = Application.Current.MainWindow };
        dlg.ShowDialog();
    }

    [RelayCommand]
    private async Task ScheduleRecordingAsync(GuideAiringCell? cell)
    {
        if (cell == null) return;
        await ScheduleAiringInternalAsync(cell);
    }

    [RelayCommand]
    private async Task UnscheduleRecordingAsync(GuideAiringCell? cell)
    {
        if (cell == null) return;
        try
        {
            await _api.UnscheduleAiringAsync(cell.Airing.Path);
            StatusText = $"Cancelled: {cell.Airing.ShowTitle}";
            cell.IsScheduled = false;
        }
        catch (Exception ex)
        {
            StatusText = $"Cancel failed: {ex.Message}";
        }
    }

    private async Task ScheduleAiringInternalAsync(GuideAiringCell cell)
    {
        Services.Logger.Log($"ScheduleAiringInternal: show={cell.Airing.ShowTitle}  path={cell.Airing.Path}  channelPath={cell.Airing.ChannelPath}");
        try
        {
            await _api.ScheduleAiringAsync(cell.Airing.Path);
            StatusText = $"Scheduled: {cell.Airing.ShowTitle}";
            Services.Logger.Log($"  Marking cell scheduled, IsScheduled was {cell.IsScheduled}");
            MarkCellScheduled(cell);
            Services.Logger.Log($"  Cell IsScheduled is now {cell.IsScheduled}");
        }
        catch (Exception ex)
        {
            Services.Logger.Log($"  EXCEPTION: {ex.GetType().Name}: {ex.Message}");
            StatusText = $"Schedule failed: {ex.Message}";
        }
    }

    private static void MarkCellScheduled(GuideAiringCell cell) => cell.IsScheduled = true;
}
