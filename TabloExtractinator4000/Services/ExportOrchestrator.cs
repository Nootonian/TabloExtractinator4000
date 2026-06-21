using System.Threading.Channels;
using TabloExtractinator4000.Models;

namespace TabloExtractinator4000.Services;

// Long-lived extraction queue. Worker loops are started once and kept running
// for the app's lifetime so new jobs can be enqueued at any time — including
// while other jobs are mid-download — without disturbing what's already running.
public class ExportOrchestrator
{
    private readonly TabloApiService _api;
    private readonly FfmpegService   _ffmpeg;
    private readonly AuditLogService _audit;

    private readonly Channel<ExportJob> _queue = Channel.CreateUnbounded<ExportJob>();
    private readonly object             _lock  = new();
    private IProgress<(ExportJob, string)>? _statusUpdate;
    private int _workerCount;

    public int MaxParallelDownloads { get; set; } = 1;

    public ExportOrchestrator(TabloApiService api, FfmpegService ffmpeg, AuditLogService audit)
    {
        _api    = api;
        _ffmpeg = ffmpeg;
        _audit  = audit;
    }

    // Adds a job to the extraction queue. Spins up worker loops on first use,
    // or scales up if MaxParallelDownloads has increased since the last call.
    public void Enqueue(ExportJob job, IProgress<(ExportJob, string)> statusUpdate)
    {
        lock (_lock)
        {
            _statusUpdate = statusUpdate;
            var target = Math.Max(1, MaxParallelDownloads);
            while (_workerCount < target)
            {
                _workerCount++;
                _ = WorkerLoopAsync();
            }
        }
        _queue.Writer.TryWrite(job);
    }

    private async Task WorkerLoopAsync()
    {
        try
        {
            await foreach (var job in _queue.Reader.ReadAllAsync())
            {
                if (job.Cts.IsCancellationRequested)
                {
                    job.State = ExportState.Cancelled;
                    _statusUpdate?.Report((job, "Cancelled."));
                    continue;
                }
                await RunSingleAsync(job, _statusUpdate, job.Cts.Token);
            }
        }
        catch (Exception ex)
        {
            // RunSingleAsync swallows its own errors — this guards against anything
            // unexpected so the slot can be replaced rather than silently lost.
            Logger.Log($"ExportOrchestrator worker crashed: {ex}");
            lock (_lock) { _workerCount--; }
        }
    }

    private async Task RunSingleAsync(
        ExportJob                       job,
        IProgress<(ExportJob, string)>? statusUpdate,
        CancellationToken               ct)
    {
        var rec = job.Recording;
        job.HasStarted = true;

        void Report(ExportState state, string msg)
        {
            job.State = state;
            statusUpdate?.Report((job, msg));
        }

        try
        {
            // ---- Step 1: Discover stream URL ----
            Report(ExportState.Discovering, "Getting stream URL…");
            var (playlistUrl, keepaliveSec) = await _api.GetPlaylistUrlAsync(rec, ct);
            job.PlaylistUrl = playlistUrl;

            // ---- Step 2: ffmpeg transcode+download ----
            job.DownloadStartedAt = DateTime.UtcNow;
            Report(ExportState.Downloading, "Downloading…");

            var downloadProgress = new Progress<(double Pct, long TotalBytes, double RateMBps)>(update =>
            {
                job.ProgressPct      = update.Pct;
                job.DownloadedBytes  = update.TotalBytes;
                job.DownloadRateMBps = update.RateMBps;
                statusUpdate?.Report((job,
                    $"Downloading {update.Pct:F1}%…"));
            });

            using var keepaliveCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var keepaliveTask = RunKeepaliveLoopAsync(rec, keepaliveSec, keepaliveCts.Token);

            try
            {
                await _ffmpeg.DownloadAsync(
                    job.PlaylistUrl,
                    job.OutputPath,
                    rec.DurationSeconds,
                    downloadProgress,
                    ct);
            }
            finally
            {
                keepaliveCts.Cancel();
                try { await keepaliveTask; } catch (OperationCanceledException) { }
            }

            job.FfmpegSuccess = true;
            var fi = new System.IO.FileInfo(job.OutputPath);
            job.OutputBytes = fi.Exists ? fi.Length : 0;

            // ---- Step 3: ffprobe verification ----
            Report(ExportState.Verifying, "Verifying…");

            var (ok, actualSec, probeError) = await _ffmpeg.VerifyAsync(
                job.OutputPath, rec.DurationSeconds, ct);

            job.OutputDurationSeconds = actualSec;
            job.ProbeVerified         = ok;

            await _audit.LogExportCompleteAsync(rec, job);

            if (!ok)
            {
                Report(ExportState.Failed, $"Verification failed: {probeError}");
                return;
            }

            Report(ExportState.Verified, $"Verified ({actualSec}s, {job.OutputBytes / 1_048_576:N0} MB)");

            // ---- Step 4: Delete from Tablo (triple-gated) ----
            // Read live — the user can toggle this per-job up until this point.
            if (!job.DeleteAfterExtraction) return;

            if (!job.FfmpegSuccess || !job.ProbeVerified)
            {
                await _audit.LogDeleteAttemptAsync(rec, job, deleteSucceeded: false,
                    "Delete skipped: ffmpeg or probe gate not passed.");
                return;
            }

            try
            {
                await _api.DeleteRecordingAsync(rec, ct);
                Report(ExportState.DeletedFromTablo, "Deleted from Tablo.");
                await _audit.LogDeleteAttemptAsync(rec, job, deleteSucceeded: true);
            }
            catch (Exception ex)
            {
                await _audit.LogDeleteAttemptAsync(rec, job, deleteSucceeded: false, ex.Message);
                Report(ExportState.Verified, $"Verified but delete failed: {ex.Message}");
            }
        }
        catch (OperationCanceledException)
        {
            Report(ExportState.Cancelled, "Cancelled.");
        }
        catch (Exception ex)
        {
            job.ErrorMessage = ex.Message;
            Report(ExportState.Failed, $"Error: {ex.Message}");
        }
    }

    private async Task RunKeepaliveLoopAsync(IRecording rec, int keepaliveSec, CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(30, keepaliveSec - 45));
        while (true)
        {
            await Task.Delay(interval, ct);
            await _api.SendKeepaliveAsync(rec, ct);
        }
    }
}
