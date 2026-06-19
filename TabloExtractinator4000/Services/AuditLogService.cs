using System.Text.Json;
using TabloExtractinator4000.Models;

namespace TabloExtractinator4000.Services;

// ---------------------------------------------------------------------------
// Append-only audit log for every delete action (attempted or completed).
// Written as newline-delimited JSON (one object per line) to audit.log
// in the same folder as the application.
//
// This log is NEVER auto-cleared. The user can open it in any text editor.
// ---------------------------------------------------------------------------
public class AuditLogService
{
    private readonly string _logPath;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public string LogPath => _logPath;

    public AuditLogService()
    {
        _logPath = System.IO.Path.Combine(AppContext.BaseDirectory, "audit.log");
    }

    public async Task LogDeleteAttemptAsync(
        IRecording recording,
        ExportJob  job,
        bool       deleteSucceeded,
        string?    failureReason = null)
    {
        var entry = new
        {
            timestamp         = DateTimeOffset.Now.ToString("O"),
            action            = "delete",
            recording_id      = recording.ObjectId,
            recording_path    = recording.Path,
            title             = recording.DisplayTitle,
            type              = recording.RecordingType,
            output_path       = job.OutputPath,
            ffmpeg_success    = job.FfmpegSuccess,
            probe_verified    = job.ProbeVerified,
            output_bytes      = job.OutputBytes,
            output_duration_s = job.OutputDurationSeconds,
            delete_succeeded  = deleteSucceeded,
            failure_reason    = failureReason,
        };

        var line = JsonSerializer.Serialize(entry);

        await _lock.WaitAsync();
        try
        {
            await System.IO.File.AppendAllTextAsync(_logPath, line + Environment.NewLine);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task LogExportCompleteAsync(IRecording recording, ExportJob job)
    {
        var entry = new
        {
            timestamp         = DateTimeOffset.Now.ToString("O"),
            action            = "export",
            recording_id      = recording.ObjectId,
            recording_path    = recording.Path,
            title             = recording.DisplayTitle,
            type              = recording.RecordingType,
            output_path       = job.OutputPath,
            ffmpeg_success    = job.FfmpegSuccess,
            probe_verified    = job.ProbeVerified,
            output_bytes      = job.OutputBytes,
            output_duration_s = job.OutputDurationSeconds,
        };

        var line = JsonSerializer.Serialize(entry);

        await _lock.WaitAsync();
        try
        {
            await System.IO.File.AppendAllTextAsync(_logPath, line + Environment.NewLine);
        }
        finally
        {
            _lock.Release();
        }
    }
}
