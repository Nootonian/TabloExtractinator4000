using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reflection;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TabloExtractinator4000.Models;
using TabloExtractinator4000.Services;

namespace TabloExtractinator4000.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly TabloAuthService    _auth;
    private readonly TabloApiService     _api;
    private readonly FfmpegService       _ffmpeg;
    private readonly FilenameService     _filenames;
    private readonly ExportOrchestrator  _orchestrator;
    private readonly SettingsService     _settings;

    // ---------------------------------------------------------------------------
    // Observable state
    // ---------------------------------------------------------------------------

    [ObservableProperty] private string  _email    = "";
    [ObservableProperty] private string  _password = "";
    [ObservableProperty] private bool    _isBusy;
    [ObservableProperty] private string  _statusMessage = "Ready";
    [ObservableProperty] private bool    _isConnected;
    [ObservableProperty] private string  _deviceLabel  = "";
    [ObservableProperty] private string  _storageLabel = "";

    // Delete is OFF by default and NOT persisted between sessions (safety gate)
    [ObservableProperty] private bool    _deleteEnabled = false;

    [ObservableProperty] private string  _outputFolder      = "";
    [ObservableProperty] private string  _movieOutputFolder = "";
    [ObservableProperty] private string  _episodeTemplate   = "";
    [ObservableProperty] private string  _movieTemplate     = "";

    [ObservableProperty] private string? _ffmpegWarning;
    [ObservableProperty] private string? _parseErrorDetails;

    [ObservableProperty] private string  _ffmpegExtraArgs       = "-c:v libx264 -preset fast -crf 23 -c:a aac";
    [ObservableProperty] private int     _maxParallelDownloads  = 1;
    [ObservableProperty] private string  _vlcPath               = "";

    partial void OnFfmpegExtraArgsChanged(string value)      => _ffmpeg.ExtraArgs                  = value;
    partial void OnMaxParallelDownloadsChanged(int value)    => _orchestrator.MaxParallelDownloads  = value;

    // Export progress list
    public ObservableCollection<ExportProgressViewModel> ExportJobs { get; } = [];

    // Keyed lookup so the progress callback can find the tile for any job,
    // regardless of which Extract/AddToExtraction call enqueued it.
    private readonly Dictionary<ExportJob, ExportProgressViewModel> _progressVms = [];
    private IProgress<(ExportJob, string)>? _exportProgress;
    private IProgress<(ExportJob, string)> ExportProgress =>
        _exportProgress ??= new Progress<(ExportJob, string)>(OnExportProgress);

    // Recording source + grouped view for DataGrid
    public ObservableCollection<RecordingRowViewModel> Recordings { get; } = [];

    private readonly CollectionViewSource _viewSource = new();
    public ICollectionView RecordingsView => _viewSource.View;

    // ---------------------------------------------------------------------------
    // Derived state
    // ---------------------------------------------------------------------------
    // "Abort Extraction" cancels every job currently in the queue/running — but
    // unlike before, extraction itself is no longer a single awaited batch, so
    // this just walks the current tiles and cancels each one's own token.
    public System.Windows.Input.ICommand CancelExportSelectedCommand => AbortAllExtractionsCommand;

    public bool CanConnect        => !IsBusy;           // allow re-connect to switch accounts
    public bool CanLoadRecordings => !IsBusy && IsConnected;
    // Exporting no longer blocks on IsBusy — the queue accepts new jobs at any time,
    // including while other recordings are mid-extraction.
    public bool CanExport         => IsConnected && Recordings.Any(r => r.IsSelected);

    public int SelectedCount => Recordings.Count(r => r.IsSelected);

    public static string AppVersion
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v is null ? "v?" : $"v{v.Major}.{v.Minor}";
        }
    }

    public MainViewModel(
        TabloAuthService   auth,
        TabloApiService    api,
        FfmpegService      ffmpeg,
        FilenameService    filenames,
        ExportOrchestrator orchestrator,
        SettingsService    settings)
    {
        _auth         = auth;
        _api          = api;
        _ffmpeg       = ffmpeg;
        _filenames    = filenames;
        _orchestrator = orchestrator;
        _settings     = settings;

        // Load all persisted settings
        var s = _settings.Load();
        _email             = s.Email;
        _password          = s.Password;
        _outputFolder      = s.OutputFolder;
        _movieOutputFolder = s.MovieOutputFolder;
        _episodeTemplate   = s.EpisodeTemplate;
        _movieTemplate     = s.MovieTemplate;
        _ffmpegExtraArgs       = s.FfmpegExtraArgs;
        _maxParallelDownloads  = s.MaxParallelDownloads;
        _vlcPath               = s.VlcPath;

        _filenames.EpisodeTemplate = _episodeTemplate;
        _filenames.MovieTemplate   = _movieTemplate;
        _ffmpeg.ExtraArgs                   = _ffmpegExtraArgs;
        _orchestrator.MaxParallelDownloads  = _maxParallelDownloads;

        // Group recordings by show
        _viewSource.Source = Recordings;
        _viewSource.GroupDescriptions.Add(
            new PropertyGroupDescription(nameof(RecordingRowViewModel.SeriesTitle)));

        // Check ffmpeg binaries
        var (ok, warn) = _ffmpeg.CheckBinaries();
        if (!ok)
            FfmpegWarning = warn;
        else if (warn != null)
            FfmpegWarning = warn;
    }

    // ---------------------------------------------------------------------------
    // Commands
    // ---------------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync(CancellationToken ct)
    {
        IsBusy        = true;
        IsConnected   = false;
        StatusMessage = "Connecting…";
        try
        {
            await _auth.LoginAsync(Email, Password, ct);

            var info    = await _api.GetServerInfoAsync(ct);
            var storage = await _api.GetStorageAsync(ct);
            DeviceLabel  = $"{info.ModelName}  •  {info.Tuners} tuners  •  v{info.Version}";
            if (storage.Count > 0)
            {
                var s = storage[0];
                var totalGb = s.TotalBytes / 1_073_741_824.0;
                var freeGb  = s.FreeBytes  / 1_073_741_824.0;
                var usedGb  = totalGb - freeGb;
                StorageLabel = $"{s.Name}  •  {usedGb:F0} / {totalGb:F0} GB used  •  {freeGb:F0} GB free";
            }
            else
            {
                StorageLabel = "No storage detected";
            }

            IsConnected   = true;
            StatusMessage = $"Connected to {info.Name} ({info.LocalAddress})";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Connection failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            ConnectCommand.NotifyCanExecuteChanged();
            LoadRecordingsCommand.NotifyCanExecuteChanged();
            ExportSelectedCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanLoadRecordings))]
    private async Task LoadRecordingsAsync(CancellationToken ct)
    {
        IsBusy = true;
        Recordings.Clear();
        foreach (var vm in ExportJobs) _progressVms.Remove(vm.Job);
        ExportJobs.Clear();
        StatusMessage = "Loading recordings…";
        try
        {
            var progress = new Progress<string>(msg => StatusMessage = msg);
            var recs     = await _api.GetAllRecordingsAsync(progress, ct);

            var sorted = recs
                .OrderBy(r => r.RecordingType)
                .ThenBy(r => r is EpisodeRecording ep ? ep.SeriesTitle : r.DisplayTitle)
                .ThenBy(r => r is EpisodeRecording ep ? ep.SeasonNumber  ?? 0 : 0)
                .ThenBy(r => r is EpisodeRecording ep ? ep.EpisodeNumber ?? 0 : 0)
                .ThenBy(r => r.AiredAt);

            foreach (var rec in sorted)
                Recordings.Add(new RecordingRowViewModel(rec));

            var loadMsg = $"Loaded {Recordings.Count} recordings.";
            if (_api.ParseErrors.Count > 0)
            {
                loadMsg += $"  ⚠ {_api.ParseErrors.Count} skipped (parse errors) — hover for details";
                ParseErrorDetails = string.Join("\n", _api.ParseErrors);
            }
            else
            {
                ParseErrorDetails = null;
            }
            StatusMessage = loadMsg;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Load failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            ExportSelectedCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(SelectedCount));
        }
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var r in Recordings) r.IsSelected = true;
        OnPropertyChanged(nameof(SelectedCount));
        ExportSelectedCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var r in Recordings) r.IsSelected = false;
        OnPropertyChanged(nameof(SelectedCount));
        ExportSelectedCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void SaveSettings()
    {
        _filenames.EpisodeTemplate = EpisodeTemplate;
        _filenames.MovieTemplate   = MovieTemplate;
        _ffmpeg.ExtraArgs          = FfmpegExtraArgs;

        _settings.Save(new AppSettings
        {
            Email                 = Email,
            Password              = Password,
            OutputFolder          = OutputFolder,
            MovieOutputFolder     = MovieOutputFolder,
            EpisodeTemplate       = EpisodeTemplate,
            MovieTemplate         = MovieTemplate,
            FfmpegExtraArgs       = FfmpegExtraArgs,
            MaxParallelDownloads  = MaxParallelDownloads,
            VlcPath               = VlcPath,
        });

        StatusMessage = "Settings saved.";
    }

    // Called by MainWindow.Loaded — auto-connect + load if credentials are present
    public async Task AutoStartAsync()
    {
        if (string.IsNullOrEmpty(Email) || string.IsNullOrEmpty(Password)) return;
        await ConnectAsync(CancellationToken.None);
        if (IsConnected)
            await LoadRecordingsAsync(CancellationToken.None);
    }

    public void OnSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        ExportSelectedCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanExport))]
    private void ExportSelected()
    {
        var selected = Recordings.Where(r => r.IsSelected).ToList();
        if (selected.Count == 0) return;
        QueueForExtraction(selected);
    }

    // Right-click "Add to extraction queue" on a single recording — same queue
    // as Extract Selected, so it slots in alongside whatever's already running.
    [RelayCommand]
    private void AddToExtraction(RecordingRowViewModel? row)
    {
        if (row == null) return;
        QueueForExtraction([row]);
        StatusMessage = $"Added to queue: {row.Recording.DisplayTitle}";
    }

    [RelayCommand]
    private void AbortAllExtractions()
    {
        foreach (var vm in ExportJobs) vm.Job.Cts.Cancel();
    }

    private void QueueForExtraction(List<RecordingRowViewModel> rows)
    {
        _filenames.EpisodeTemplate = EpisodeTemplate;
        _filenames.MovieTemplate   = MovieTemplate;

        var jobs = rows.Select(row =>
        {
            var filename   = _filenames.GenerateFilename(row.Recording);
            var baseFolder = row.Recording is MovieRecording ? MovieOutputFolder : OutputFolder;
            var outPath    = System.IO.Path.Combine(baseFolder, filename);
            return new ExportJob(row.Recording, outPath) { DeleteAfterExtraction = DeleteEnabled };
        }).ToList();

        var toDelete = jobs.Where(j => j.DeleteAfterExtraction).ToList();
        if (toDelete.Count > 0 && !ShowDeleteConfirmation(toDelete)) return;

        // Starting fresh while the queue is idle clears old finished tiles. While
        // jobs are already running, new ones are appended alongside them instead.
        if (!IsBusy)
        {
            foreach (var vm in ExportJobs) _progressVms.Remove(vm.Job);
            ExportJobs.Clear();
        }

        foreach (var job in jobs)
        {
            var vm = new ExportProgressViewModel(job);
            _progressVms[job] = vm;
            ExportJobs.Add(vm);
            _orchestrator.Enqueue(job, ExportProgress);
        }

        RecomputeIsBusy();
    }

    private void OnExportProgress((ExportJob job, string msg) update)
    {
        var (job, msg) = update;
        if (!_progressVms.TryGetValue(job, out var vm)) return;

        // A tile cancelled before it ever started downloading is removed
        // outright rather than left showing a "Cancelled" state.
        if (job.State == ExportState.Cancelled && !job.HasStarted)
        {
            ExportJobs.Remove(vm);
            _progressVms.Remove(job);
            RecomputeIsBusy();
            return;
        }

        vm.Status      = msg;
        vm.ProgressPct = job.ProgressPct;
        vm.State       = job.State.ToString();
        vm.BytesText   = job.DownloadedBytes > 0 ? $"{job.DownloadedBytes / 1_048_576.0:F0} MB" : "";
        vm.RateText    = job.DownloadRateMBps > 0 ? $"{job.DownloadRateMBps:F1} MB/s" : "";

        // Time remaining estimate: elapsed / pct × remaining pct
        if (job.ProgressPct > 1.0)
        {
            var elapsed = (DateTime.UtcNow - job.DownloadStartedAt).TotalSeconds;
            var remaining = elapsed / (job.ProgressPct / 100.0) - elapsed;
            vm.TimeRemainingText = remaining > 5 ? FormatTimeRemaining(remaining) : "";
        }
        else
        {
            vm.TimeRemainingText = "";
        }

        var totalElapsed = (DateTime.UtcNow - job.DownloadStartedAt).TotalSeconds;
        vm.ElapsedText = FormatElapsed(totalElapsed);

        // Per-job completion side effects — previously done once after the whole
        // batch finished; now each job can finish independently of the others.
        if (job.State == ExportState.Verified && !job.DeleteAfterExtraction)
        {
            var row = Recordings.FirstOrDefault(r => r.Recording == job.Recording);
            if (row is { IsSelected: true })
            {
                row.IsSelected = false;
                OnPropertyChanged(nameof(SelectedCount));
                ExportSelectedCommand.NotifyCanExecuteChanged();
            }
        }

        // Pull the row out directly rather than reloading — a full reload would
        // also clear ExportJobs and disrupt any other tiles still in progress.
        if (job.State == ExportState.DeletedFromTablo)
        {
            var deletedRow = Recordings.FirstOrDefault(r => r.Recording == job.Recording);
            if (deletedRow != null)
            {
                Recordings.Remove(deletedRow);
                OnPropertyChanged(nameof(SelectedCount));
                ExportSelectedCommand.NotifyCanExecuteChanged();
            }
        }

        RecomputeIsBusy();
    }

    private void RecomputeIsBusy()
    {
        IsBusy = ExportJobs.Any(vm => vm.Job.State is ExportState.Pending or ExportState.Discovering
                                                    or ExportState.Downloading or ExportState.Verifying);
        if (!IsBusy)
            StatusMessage = ExportJobs.Any(vm => vm.Job.State == ExportState.Failed)
                ? "Extraction complete — some failed, see tiles."
                : ExportJobs.Count > 0 ? "Extraction complete." : "Ready";
        else
            StatusMessage = "Extracting…";
    }

    [RelayCommand]
    private void BrowseOutputFolder()
    {
        var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select episode output folder", SelectedPath = OutputFolder, ShowNewFolderButton = true
        };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            OutputFolder = dialog.SelectedPath;
    }

    [RelayCommand]
    private void BrowseMovieFolder()
    {
        var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select movie output folder", SelectedPath = MovieOutputFolder, ShowNewFolderButton = true
        };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            MovieOutputFolder = dialog.SelectedPath;
    }

    private bool ShowDeleteConfirmation(List<ExportJob> jobs)
    {
        var list   = string.Join("\n", jobs.Select(j => $"  • {j.Recording.DisplayTitle}"));
        var result = System.Windows.MessageBox.Show(
            $"The following {jobs.Count} recording(s) will be PERMANENTLY DELETED from the Tablo " +
            $"after successful extraction and verification:\n\n{list}\n\nThis cannot be undone. Continue?",
            "Confirm Delete After Extraction",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);
        return result == System.Windows.MessageBoxResult.Yes;
    }

    // Master switch mass-sets every current tile's checkbox; tiles can still be
    // flipped individually afterward, even mid-extraction.
    partial void OnDeleteEnabledChanged(bool value)
    {
        foreach (var vm in ExportJobs) vm.DeleteAfterExtraction = value;
    }

    [RelayCommand]
    private void BrowseVlcPath()
    {
        var dialog = new System.Windows.Forms.OpenFileDialog
        {
            Title  = "Find VLC executable",
            Filter = "vlc.exe|vlc.exe|All executables|*.exe",
            FileName = "vlc.exe",
        };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            VlcPath = dialog.FileName;
    }

    [RelayCommand]
    private async Task PlayRecordingAsync(RecordingRowViewModel row)
    {
        if (string.IsNullOrWhiteSpace(VlcPath) || !System.IO.File.Exists(VlcPath))
        {
            StatusMessage = "VLC not found — set the path in Settings.";
            return;
        }

        StatusMessage = $"Opening stream for {row.Recording.DisplayTitle}…";
        try
        {
            var (playlistUrl, keepaliveSec) = await _api.GetPlaylistUrlAsync(row.Recording, CancellationToken.None);

            System.Windows.Clipboard.SetText(playlistUrl);

            var psi = new System.Diagnostics.ProcessStartInfo(VlcPath)
            {
                UseShellExecute  = false,
                WorkingDirectory = System.IO.Path.GetDirectoryName(VlcPath) ?? "",
            };
            psi.ArgumentList.Add(playlistUrl);
            psi.ArgumentList.Add("--volume=25");
            var vlcProc = System.Diagnostics.Process.Start(psi)!;
            StatusMessage = $"Playing: {row.Recording.DisplayTitle}  (URL copied to clipboard)";

            // Run keepalive in background until VLC exits
            _ = Task.Run(async () =>
            {
                var interval = TimeSpan.FromSeconds(Math.Max(30, keepaliveSec - 45));
                while (!vlcProc.HasExited)
                {
                    await Task.Delay(interval);
                    if (!vlcProc.HasExited)
                        await _api.SendKeepaliveAsync(row.Recording, CancellationToken.None);
                }
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"Play failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ShowDetailsAsync(RecordingRowViewModel row)
    {
        var r  = row.Recording;
        var sb = new System.Text.StringBuilder();

        sb.AppendLine($"Title:      {r.DisplayTitle}");
        sb.AppendLine($"Type:       {r.RecordingType}");

        if (r is EpisodeRecording ep)
        {
            if (!string.IsNullOrEmpty(ep.Description))
                sb.AppendLine($"Description: {ep.Description}");
            if (ep.OriginalAirDate != DateTimeOffset.MinValue)
                sb.AppendLine($"Orig. air:  {ep.OriginalAirDate:yyyy-MM-dd}");
        }
        if (r is MovieRecording mv && !string.IsNullOrEmpty(mv.FilmRating))
            sb.AppendLine($"Rating:     {mv.FilmRating}");
        if (r is SportRecording sp && !string.IsNullOrEmpty(sp.LeagueTitle))
            sb.AppendLine($"League:     {sp.LeagueTitle}");

        sb.AppendLine();
        sb.AppendLine($"Channel:    {r.ChannelMajor}.{r.ChannelMinor}  {r.NetworkName}");
        sb.AppendLine($"Aired:      {r.AiredAt.LocalDateTime:yyyy-MM-dd  h:mm tt}");
        sb.AppendLine($"Duration:   {FormatDur(r.DurationSeconds)} recorded  /  {FormatDur(r.ScheduledSeconds)} scheduled");
        sb.AppendLine();
        sb.AppendLine($"Resolution: {r.Width}×{r.Height}{(r.IsInterlaced ? "i" : "p")}");
        sb.AppendLine($"Audio:      {r.AudioFormat.ToUpper()}");
        sb.AppendLine($"Container:  {r.ContainerFormat}");
        sb.AppendLine($"Size:       {r.SizeBytes / 1_048_576.0:F0} MB");
        sb.AppendLine();
        sb.AppendLine($"State:      {r.State}");
        sb.AppendLine($"Watched:    {(r.Watched ? "Yes" : "No")}");

        sb.AppendLine();
        sb.AppendLine("── Stream URL ──────────────────────────────────────────");
        try
        {
            var (url, _) = await _api.GetPlaylistUrlAsync(r, CancellationToken.None);
            sb.AppendLine(url);
        }
        catch (Exception ex)
        {
            sb.AppendLine($"(could not fetch: {ex.Message})");
        }

        var win = new TabloExtractinator4000.Views.DetailsWindow(r.DisplayTitle, sb.ToString())
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        win.Show();
    }

    private static string FormatDur(int seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}h {ts.Minutes:00}m"
            : $"{ts.Minutes}m";
    }

    [RelayCommand]
    private async Task DeleteRecordingAsync(RecordingRowViewModel row)
    {
        var result = System.Windows.MessageBox.Show(
            $"Permanently delete \"{row.Recording.DisplayTitle}\" from the Tablo?\n\nThis cannot be undone.",
            "Confirm Delete",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);

        if (result != System.Windows.MessageBoxResult.Yes) return;

        try
        {
            await _api.DeleteRecordingAsync(row.Recording, CancellationToken.None);
            Recordings.Remove(row);
            OnPropertyChanged(nameof(SelectedCount));
            ExportSelectedCommand.NotifyCanExecuteChanged();
            StatusMessage = $"Deleted: {row.Recording.DisplayTitle}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Delete failed: {ex.Message}";
        }
    }

    private static string FormatTimeRemaining(double seconds)
    {
        if (seconds >= 3600) return $"{(int)(seconds / 3600)}h {(int)(seconds % 3600 / 60)}m remaining";
        if (seconds >= 60)   return $"{(int)(seconds / 60)}m {(int)(seconds % 60)}s remaining";
        return $"{(int)seconds}s remaining";
    }

    private static string FormatElapsed(double seconds)
    {
        if (seconds >= 3600) return $"{(int)(seconds / 3600)}h {(int)(seconds % 3600 / 60)}m {(int)(seconds % 60)}s elapsed";
        if (seconds >= 60)   return $"{(int)(seconds / 60)}m {(int)(seconds % 60)}s elapsed";
        return $"{(int)seconds}s elapsed";
    }
}
