using System.Diagnostics;

namespace TabloExtractinator4000.Services;

public class FfmpegService
{
    private static string ToolsDir =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "tools");

    public string FfmpegPath  => System.IO.Path.Combine(ToolsDir, "ffmpeg.exe");
    public string FfprobePath => System.IO.Path.Combine(ToolsDir, "ffprobe.exe");

    // Free-form ffmpeg arguments inserted between -i <url> and the output path.
    // The app always manages: -i <url>  ...ExtraArgs...  -movflags +faststart -y -progress pipe:1 -loglevel error <output>
    public string ExtraArgs { get; set; } = "-c:v libx264 -preset fast -crf 23 -c:a aac";

    // Progress tuple: percent complete, total output bytes written, MB/s rate
    public async Task DownloadAsync(
        string          playlistUrl,
        string          outputPath,
        int             expectedDurationSeconds,
        IProgress<(double Pct, long TotalBytes, double RateMBps)>? progress,
        CancellationToken ct = default)
    {
        var dir = System.IO.Path.GetDirectoryName(outputPath);
        if (dir != null) System.IO.Directory.CreateDirectory(dir);

        // Use ArgumentList so each argument is individually quoted — no shell escaping issues.
        // Always encode as H.264 (libx264) — the gyan.dev essentials build always includes it.
        var psi = new ProcessStartInfo(FfmpegPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };

        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(playlistUrl);
        foreach (var token in SplitArgs(ExtraArgs))
            psi.ArgumentList.Add(token);
        psi.ArgumentList.Add("-movflags"); psi.ArgumentList.Add("+faststart");
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-progress"); psi.ArgumentList.Add("pipe:1");
        psi.ArgumentList.Add("-loglevel");  psi.ArgumentList.Add("error");
        psi.ArgumentList.Add(outputPath);

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stderr = new System.Text.StringBuilder();
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

        proc.Start();
        proc.BeginErrorReadLine();

        try
        {
            // ffmpeg -progress emits key=value blocks terminated by "progress=continue|end".
            // Collect out_time_us and total_size within each block; report on "progress=" line.
            long outTimeUs   = 0;
            long totalSize   = 0;
            long prevSize    = 0;
            var  prevTime    = DateTime.UtcNow;

            await Task.Run(async () =>
            {
                string? line;
                while ((line = await proc.StandardOutput.ReadLineAsync(ct)) != null)
                {
                    if (line.StartsWith("out_time_us=", StringComparison.Ordinal))
                        long.TryParse(line[12..], out outTimeUs);
                    else if (line.StartsWith("total_size=", StringComparison.Ordinal))
                        long.TryParse(line[11..], out totalSize);
                    else if (line.StartsWith("progress=", StringComparison.Ordinal) && outTimeUs > 0)
                    {
                        var pct = expectedDurationSeconds > 0
                            ? Math.Min(100.0, outTimeUs / 1_000_000.0 / expectedDurationSeconds * 100.0)
                            : 0;

                        var now     = DateTime.UtcNow;
                        var elapsed = (now - prevTime).TotalSeconds;
                        var rate    = elapsed > 0.2 && totalSize > prevSize
                            ? (totalSize - prevSize) / elapsed / 1_048_576.0
                            : 0;

                        prevSize = totalSize;
                        prevTime = now;

                        progress?.Report((pct, totalSize, rate));
                    }
                }
            }, ct);

            await proc.WaitForExitAsync(ct);
        }
        finally
        {
            if (!proc.HasExited)
                try { proc.Kill(entireProcessTree: true); } catch { }
        }

        if (proc.ExitCode != 0 && !ct.IsCancellationRequested)
            throw new InvalidOperationException(
                $"ffmpeg exited {proc.ExitCode}.\nstderr:\n{stderr}");

        progress?.Report((100.0, 0L, 0.0));
    }

    public async Task<(bool Success, int ActualSeconds, string? Error)> VerifyAsync(
        string outputPath, int expectedDurationSeconds, CancellationToken ct = default)
    {
        if (!System.IO.File.Exists(outputPath))
            return (false, 0, "Output file does not exist.");

        var psi  = new ProcessStartInfo(FfprobePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        psi.ArgumentList.Add("-v");                    psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-show_entries");          psi.ArgumentList.Add("format=duration");
        psi.ArgumentList.Add("-of");                    psi.ArgumentList.Add("default=noprint_wrappers=1:nokey=1");
        psi.ArgumentList.Add(outputPath);

        using var proc = new Process { StartInfo = psi };
        proc.Start();

        var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = await proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);

        if (proc.ExitCode != 0)
            return (false, 0, $"ffprobe error: {stderr.Trim()}");

        if (!double.TryParse(stdout.Trim(),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var actualSec))
            return (false, 0, $"Could not parse ffprobe output: '{stdout.Trim()}'");

        var actual    = (int)Math.Round(actualSec);
        var tolerance = Math.Max(60, expectedDurationSeconds * 0.05);
        var diff      = Math.Abs(actual - expectedDurationSeconds);

        if (diff > tolerance)
            return (false, actual,
                $"Duration mismatch: got {actual}s, expected {expectedDurationSeconds}s (tolerance {tolerance:F0}s)");

        return (true, actual, null);
    }

    // Checks that the binaries exist and that libx264 is compiled in.
    // Returns (ok, warningMessage).
    public (bool Ok, string? Warning) CheckBinaries()
    {
        if (!System.IO.File.Exists(FfmpegPath))
            return (false, $"ffmpeg.exe not found at: {FfmpegPath}");
        if (!System.IO.File.Exists(FfprobePath))
            return (false, $"ffprobe.exe not found at: {FfprobePath}");

        try
        {
            var psi = new ProcessStartInfo(FfmpegPath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };
            psi.ArgumentList.Add("-encoders");
            psi.ArgumentList.Add("-v");
            psi.ArgumentList.Add("quiet");

            using var proc = Process.Start(psi)!;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();

            if (!output.Contains("libx264", StringComparison.OrdinalIgnoreCase))
                return (true,
                    "libx264 not found in this ffmpeg build. Extraction will fail.\n" +
                    "Download the GPL build from https://www.gyan.dev/ffmpeg/builds/ " +
                    "(ffmpeg-release-full.7z) and replace tools\\ffmpeg.exe.");
        }
        catch
        {
            // If encoder probe fails, proceed and let the first extraction attempt surface the error.
        }

        return (true, null);
    }

    // Splits a command-line string into individual tokens, respecting double-quoted spans.
    public static IEnumerable<string> SplitArgs(string args)
    {
        var result = new List<string>();
        var token  = new System.Text.StringBuilder();
        bool inQuote = false;

        foreach (char c in args)
        {
            if (c == '"')
            {
                inQuote = !inQuote;
            }
            else if (char.IsWhiteSpace(c) && !inQuote)
            {
                if (token.Length > 0)
                {
                    result.Add(token.ToString());
                    token.Clear();
                }
            }
            else
            {
                token.Append(c);
            }
        }

        if (token.Length > 0) result.Add(token.ToString());
        return result;
    }
}
