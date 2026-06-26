using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using Line = System.Windows.Shapes.Line;
using Polyline = System.Windows.Shapes.Polyline;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace CommercialCutter;

public partial class MainWindow : Window
{
    private string? _videoPath;
    private string? _workDir;
    private string? _configPath;
    private string? _thumbsDir;
    private string? _segmentsPath;
    private double  _durationSeconds;
    private bool    _nvencAvailable;
    private CancellationTokenSource? _cts;

    public ObservableCollection<SegmentRow> Segments { get; } = new();
    public ObservableCollection<CutMapRow> CutMap { get; } = new();

    private AppSettings _settings = new();

    // Cached results of the most recent analysis, kept around so "Export Diagnostics" can dump
    // them without re-running anything — useful for comparing against manually-identified break
    // timestamps to see where the detection agrees/disagrees.
    private List<SampleScore>? _lastScores;
    private List<(double Start, double End)>? _lastBlack;
    private List<(double Start, double End)>? _lastSilence;
    private List<Segment>? _lastSegments;
    private double[]? _lastThresholdCurve;
    private double _lastInterval;

    public MainWindow()
    {
        InitializeComponent();
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        if (version is not null) Title = $"Commercial Cutter v{version.Major}.{version.Minor}.{version.Build}";
        SegmentsGrid.ItemsSource = Segments;
        CutMapGrid.ItemsSource = CutMap;

        _settings = SettingsStore.Load();
        ApplySettingsToUi(_settings);
        CutMode_Changed(this, new RoutedEventArgs());
        DetectionMode_Changed(this, new RoutedEventArgs());

        Closing += (_, _) => SaveSettingsFromUi();
    }

    private void ApplySettingsToUi(AppSettings s)
    {
        IntervalBox.Text          = s.IntervalSeconds.ToString(CultureInfo.InvariantCulture);
        AdUnitSecondsBox.Text     = s.AdUnitSeconds.ToString(CultureInfo.InvariantCulture);
        MinBreakSecondsBox.Text   = s.MinBreakSeconds.ToString(CultureInfo.InvariantCulture);
        RefineTransitionsCheckBox.IsChecked = s.RefineTransitions;
        MaxNudgeSecondsBox.Text   = s.MaxNudgeSeconds.ToString(CultureInfo.InvariantCulture);
        HybridToleranceBox.Text   = s.HybridToleranceSeconds.ToString(CultureInfo.InvariantCulture);
        LocalWindowSecondsBox.Text = s.LocalWindowSeconds.ToString(CultureInfo.InvariantCulture);
        if (s.ExpectedLengthMinutes is { } expectedLength) ExpectedLengthBox.Text = expectedLength.ToString(CultureInfo.InvariantCulture);
        if (s.ManualThreshold is { } manualThresholdSetting) ThresholdBox.Text = manualThresholdSetting.ToString(CultureInfo.InvariantCulture);
        if (s.ManualDip is { } manualDipSetting) AdaptiveDipBox.Text = manualDipSetting.ToString(CultureInfo.InvariantCulture);
        TrimEdgePromosCheckBox.IsChecked = s.TrimEdgePromos;
        MaxPromoSecondsBox.Text = s.MaxPromoSeconds.ToString(CultureInfo.InvariantCulture);

        if (s.DetectionMode == "Absolute") AbsoluteModeRadio.IsChecked = true;
        else AdaptiveModeRadio.IsChecked = true;

        switch (s.CutMode)
        {
            case "Fast":   FastCutRadio.IsChecked   = true; break;
            case "Hybrid": HybridCutRadio.IsChecked = true; break;
            default:       ExactCutRadio.IsChecked  = true; break;
        }
    }

    private void SaveSettingsFromUi()
    {
        double.TryParse(IntervalBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var interval);
        double.TryParse(AdUnitSecondsBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var adUnit);
        double.TryParse(MinBreakSecondsBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var minBreak);
        double.TryParse(MaxNudgeSecondsBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var maxNudge);
        double.TryParse(HybridToleranceBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var hybridTol);
        double.TryParse(LocalWindowSecondsBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var localWindow);

        _settings.IntervalSeconds       = interval > 0 ? interval : _settings.IntervalSeconds;
        _settings.AdUnitSeconds         = adUnit > 0 ? adUnit : _settings.AdUnitSeconds;
        _settings.MinBreakSeconds       = minBreak >= 0 ? minBreak : _settings.MinBreakSeconds;
        _settings.RefineTransitions     = RefineTransitionsCheckBox.IsChecked == true;
        _settings.MaxNudgeSeconds       = maxNudge >= 0 ? maxNudge : _settings.MaxNudgeSeconds;
        _settings.HybridToleranceSeconds = hybridTol >= 0 ? hybridTol : _settings.HybridToleranceSeconds;
        _settings.LocalWindowSeconds    = localWindow > 0 ? localWindow : _settings.LocalWindowSeconds;
        _settings.CutMode = FastCutRadio.IsChecked == true ? "Fast" : HybridCutRadio.IsChecked == true ? "Hybrid" : "Exact";
        _settings.DetectionMode = AbsoluteModeRadio.IsChecked == true ? "Absolute" : "Adaptive";

        _settings.ExpectedLengthMinutes = double.TryParse(ExpectedLengthBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var expectedLength) ? expectedLength : null;
        _settings.ManualThreshold       = double.TryParse(ThresholdBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var manualThresholdValue) ? manualThresholdValue : null;
        _settings.ManualDip             = double.TryParse(AdaptiveDipBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var manualDipValue) ? manualDipValue : null;
        _settings.TrimEdgePromos        = TrimEdgePromosCheckBox.IsChecked == true;
        if (double.TryParse(MaxPromoSecondsBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var maxPromo) && maxPromo > 0)
            _settings.MaxPromoSeconds = maxPromo;

        if (!string.IsNullOrEmpty(_videoPath))
            _settings.LastVideoFolder = Path.GetDirectoryName(_videoPath);
        if (!string.IsNullOrEmpty(BatchFolderBox.Text))
            _settings.LastBatchFolder = BatchFolderBox.Text;

        SettingsStore.Save(_settings);
    }

    private void DetectionMode_Changed(object sender, RoutedEventArgs e)
    {
        if (AdaptiveModePanel is null) return; // fires during InitializeComponent before fields exist
        var adaptive = AdaptiveModeRadio.IsChecked == true;
        AdaptiveModePanel.Visibility = adaptive ? Visibility.Visible : Visibility.Collapsed;
        AbsoluteModePanel.Visibility = adaptive ? Visibility.Collapsed : Visibility.Visible;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var hwnd = new WindowInteropHelper(this).Handle;
        int useDarkMode = 1;
        // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE on Windows 10 20H1+/11; 19 is the pre-20H1 value.
        if (DwmSetWindowAttribute(hwnd, 20, ref useDarkMode, sizeof(int)) != 0)
            DwmSetWindowAttribute(hwnd, 19, ref useDarkMode, sizeof(int));
    }

    private void SetProgress(double pct, string label)
    {
        FfmpegProgressBar.Value = pct;
        ProgressLabel.Text = label;
    }

    // Wraps a long-running operation: shows the abort button, drives the progress bar,
    // and resets both when the operation finishes (successfully, on error, or cancelled).
    private async Task RunWithProgressAsync(Func<IProgress<double>, CancellationToken, Task> work, string busyLabel)
    {
        _cts = new CancellationTokenSource();
        AbortButton.IsEnabled = true;
        SetProgress(0, busyLabel);
        var progress = new Progress<double>(pct => SetProgress(pct, $"{pct:F0}%"));

        try
        {
            await work(progress, _cts.Token);
        }
        finally
        {
            AbortButton.IsEnabled = false;
            _cts.Dispose();
            _cts = null;
            SetProgress(0, "Idle");
        }
    }

    private void Abort_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        Log("Abort requested...");
    }

    private void Log(string message) =>
        Dispatcher.Invoke(() =>
        {
            LogBox.AppendText($"{DateTime.Now:HH:mm:ss}  {message}\n");
            LogBox.ScrollToEnd();
        });

    private async void BrowseVideo_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Choose a recorded video file",
            Filter = "Video files|*.mp4;*.mkv;*.ts;*.mts;*.m2ts;*.avi|All files|*.*",
            InitialDirectory = _settings.LastVideoFolder ?? "",
        };
        if (dlg.ShowDialog(this) != true) return;

        _videoPath = dlg.FileName;
        VideoPathBox.Text = _videoPath;
        VideoInfoText.Text = "Probing duration...";

        _workDir      = LocalPaths.GetWorkDir(_videoPath);
        _configPath   = Path.Combine(_workDir, "config.json");
        _thumbsDir    = Path.Combine(_workDir, "thumbs");
        _segmentsPath = Path.Combine(_workDir, "segments.json");
        OutputPathBox.Text = Path.Combine(Path.GetDirectoryName(_videoPath)!,
            Path.GetFileNameWithoutExtension(_videoPath) + "_clean.mp4");

        try
        {
            _durationSeconds = await Ffmpeg.GetDurationSecondsAsync(_videoPath);
            VideoInfoText.Text = $"Duration: {TimeSpan.FromSeconds(_durationSeconds):hh\\:mm\\:ss}   " +
                                 $"Working folder: {_workDir}";
            SampleAtBox.Text = ((int)(_durationSeconds * 0.25)).ToString(CultureInfo.InvariantCulture);
            SelectBoxButton.IsEnabled = true;
            Log($"Loaded {Path.GetFileName(_videoPath)} ({_durationSeconds:F0}s).");

            _nvencAvailable = await Ffmpeg.IsNvencAvailableAsync();
            NvencInfoText.Text = _nvencAvailable
                ? "GPU encode: NVENC available"
                : "GPU encode: NVENC not found — falling back to libx264 (slower)";
        }
        catch (Exception ex)
        {
            VideoInfoText.Text = "Could not read duration.";
            Log($"ffprobe failed: {ex.Message}");
        }
    }

    private async void SelectBox_Click(object sender, RoutedEventArgs e)
    {
        if (_videoPath is null || _configPath is null) return;
        if (!double.TryParse(SampleAtBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var sampleAt))
        {
            Log("Enter a valid number of seconds to sample at.");
            return;
        }

        SelectBoxButton.IsEnabled = false;
        try
        {
            Log($"Extracting frame at {sampleAt:F0}s...");
            var framePath = Path.Combine(_workDir!, "selectbox_frame.jpg");
            await Cataloger.ExtractSingleFrameAsync(_videoPath, sampleAt, framePath);

            var window = new BoxSelectWindow(_videoPath, framePath, sampleAt, _durationSeconds);
            window.Owner = this;
            var ok = window.ShowDialog();
            if (ok != true || window.Result is null)
            {
                Log("Box selection cancelled.");
                return;
            }

            var crop = window.Result;
            var referencePath = Path.Combine(_workDir!, "reference_logo.jpg");
            await Cataloger.CatalogReferenceCropAsync(_videoPath, sampleAt, crop, referencePath);

            var (w, h) = await GetFrameSizeAsync(_videoPath);
            ConfigStore.SaveConfig(_configPath, new CutterConfig(crop, referencePath, w, h));

            BoxInfoText.Text = $"Box saved: x={crop.X} y={crop.Y} w={crop.Width} h={crop.Height}";
            Log("Logo box saved.");
            AnalyzeButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            Log($"Box selection failed: {ex.Message}");
        }
        finally
        {
            SelectBoxButton.IsEnabled = true;
        }
    }

    private static async Task<(int Width, int Height)> GetFrameSizeAsync(string video)
    {
        var (code, stdout, stderr) = await Ffmpeg.RunFfprobeAsync(new[]
        {
            "-v", "error",
            "-select_streams", "v:0",
            "-show_entries", "stream=width,height",
            "-of", "csv=p=0",
            video,
        });
        if (code != 0) throw new InvalidOperationException($"ffprobe failed: {stderr}");
        var parts = stdout.Trim().Split(',');
        return (int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture));
    }

    // Plots the raw similarity-to-reference curve, the effective cutoff (constant for absolute
    // mode, a moving local-baseline-minus-drop curve for adaptive mode), and the resulting
    // segment classification, so it's visible at a glance whether the cutoff is cutting cleanly
    // through the noise or splitting a flat/ambiguous region.
    // Recomputed whenever the segment list changes or a row's "Is Commercial" box is toggled,
    // so the cut-map table always reflects what would actually happen if you hit Cut Video now.
    private void RecomputeCutMap()
    {
        CutMap.Clear();
        double outputPosition = 0;
        foreach (var row in Segments)
        {
            var seg = row.ToSegment();
            if (seg.IsCommercial)
                CutMap.Add(new CutMapRow(outputPosition, seg.StartSeconds, seg.EndSeconds));
            else
                outputPosition += seg.DurationSeconds;
        }
    }

    private void DrawSimilarityChart(List<SampleScore> scores, double[] thresholdCurve, List<Segment> segments)
    {
        SimilarityChart.Children.Clear();
        if (scores.Count == 0) return;

        var width  = SimilarityChart.ActualWidth  > 0 ? SimilarityChart.ActualWidth  : 840;
        var height = SimilarityChart.ActualHeight > 0 ? SimilarityChart.ActualHeight : 120;
        var interval = scores.Count > 1 ? scores[1].TimeSeconds - scores[0].TimeSeconds : 1.0;
        var totalTime = scores[^1].TimeSeconds + interval;

        double XForTime(double t) => t / totalTime * width;
        double YForSimilarity(double s) => height - Math.Clamp(s, 0, 1) * height;

        foreach (var seg in segments)
        {
            var rect = new Rectangle
            {
                Width  = Math.Max(0, XForTime(seg.EndSeconds) - XForTime(seg.StartSeconds)),
                Height = height,
                Fill   = new SolidColorBrush(seg.IsCommercial
                    ? Color.FromArgb(60, 217, 60, 60)
                    : Color.FromArgb(40, 76, 175, 80)),
            };
            Canvas.SetLeft(rect, XForTime(seg.StartSeconds));
            Canvas.SetTop(rect, 0);
            SimilarityChart.Children.Add(rect);
        }

        var thresholdLine = new Polyline
        {
            Stroke = Brushes.White,
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 4, 3 },
            Opacity = 0.6,
        };
        var thresholdPoints = new PointCollection();
        for (int i = 0; i < scores.Count; i++)
            thresholdPoints.Add(new Point(XForTime(scores[i].TimeSeconds), YForSimilarity(thresholdCurve[i])));
        thresholdLine.Points = thresholdPoints;
        SimilarityChart.Children.Add(thresholdLine);

        var curve = new Polyline
        {
            Stroke = new SolidColorBrush(Color.FromRgb(0x4F, 0xC3, 0xF7)),
            StrokeThickness = 1.5,
        };
        var points = new PointCollection();
        foreach (var s in scores)
            points.Add(new Point(XForTime(s.TimeSeconds), YForSimilarity(s.Similarity)));
        curve.Points = points;
        SimilarityChart.Children.Add(curve);
    }

    private async void Analyze_Click(object sender, RoutedEventArgs e)
    {
        if (_videoPath is null || _configPath is null || _thumbsDir is null || _segmentsPath is null) return;
        if (!double.TryParse(IntervalBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var interval) || interval <= 0)
        {
            Log("Enter a valid sample interval.");
            return;
        }

        var isAdaptive = AdaptiveModeRadio.IsChecked == true;
        var manualDip = 0.0;
        var manualThreshold = 0.0;
        var hasManualThreshold = isAdaptive
            ? double.TryParse(AdaptiveDipBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out manualDip)
            : double.TryParse(ThresholdBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out manualThreshold);
        var hasExpectedLength  = double.TryParse(ExpectedLengthBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var expectedMinutes);

        if (!hasManualThreshold && !hasExpectedLength)
        {
            Log(isAdaptive
                ? "Enter either an expected program length or a manual max-dip value."
                : "Enter either an expected program length or a manual similarity threshold.");
            return;
        }
        var localWindowSeconds = 300.0;
        if (isAdaptive &&
            (!double.TryParse(LocalWindowSecondsBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out localWindowSeconds) || localWindowSeconds <= 0))
        {
            Log("Enter a valid local window radius.");
            return;
        }
        if (!double.TryParse(AdUnitSecondsBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var adUnitSeconds) || adUnitSeconds <= 0)
        {
            Log("Enter a valid ad unit length.");
            return;
        }
        if (!double.TryParse(MinBreakSecondsBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var minBreakSeconds) || minBreakSeconds < 0)
        {
            Log("Enter a valid minimum break length.");
            return;
        }
        var refineTransitions = RefineTransitionsCheckBox.IsChecked == true;
        var maxNudgeSeconds = 0.0;
        if (refineTransitions &&
            (!double.TryParse(MaxNudgeSecondsBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out maxNudgeSeconds) || maxNudgeSeconds < 0))
        {
            Log("Enter a valid max nudge (seconds) for transition refinement.");
            return;
        }
        var trimEdgePromos = TrimEdgePromosCheckBox.IsChecked == true;
        var maxPromoSeconds = 90.0;
        if (trimEdgePromos &&
            (!double.TryParse(MaxPromoSecondsBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out maxPromoSeconds) || maxPromoSeconds <= 0))
        {
            Log("Enter a valid max promo length (seconds).");
            return;
        }

        AnalyzeButton.IsEnabled = false;
        try
        {
            await RunWithProgressAsync(async (progress, ct) =>
            {
                var config = ConfigStore.LoadConfig(_configPath);

                AnalyzeInfoText.Text = "Cataloging thumbnails... this can take a while for long recordings.";
                Log("Extracting logo-region thumbnails...");
                await Cataloger.CatalogAsync(_videoPath, config.Crop, _thumbsDir, interval, _durationSeconds, progress, ct);

                Log("Scoring thumbnails against reference...");
                var scores = await Task.Run(() => Analyzer.ScoreThumbnails(_thumbsDir, config.ReferenceImagePath, interval), ct);

                List<(double Start, double End)>? black = null, silence = null;
                if (refineTransitions || trimEdgePromos)
                {
                    Log("Scanning for black/silent transitions (full decode pass)...");
                    (black, silence) = await Ffmpeg.DetectBlackAndSilenceAsync(_videoPath, _durationSeconds, progress, ct);
                    Log($"Found {black.Count} black dip(s), {silence.Count} silent dip(s).");
                }

                List<Segment> segments;
                string usedThresholdLabel;
                double[] thresholdCurve;
                if (hasExpectedLength)
                {
                    Log($"Searching for a {(isAdaptive ? "local dip" : "threshold")} that yields ~{expectedMinutes:F0} min of program...");
                    if (isAdaptive)
                    {
                        var (found, drop) = Analyzer.FindAdaptiveThresholdForTarget(
                            scores, interval, expectedMinutes * 60.0, localWindowSeconds, adUnitSeconds, minBreakSeconds, black, silence, maxNudgeSeconds);
                        segments = found;
                        usedThresholdLabel = $"local baseline − {drop:F3}";
                        var baseline = Analyzer.ComputeLocalBaseline(scores, interval, localWindowSeconds);
                        thresholdCurve = baseline.Select(b => b - drop).ToArray();
                        Log($"Settled on max dip {drop:F3} from local baseline.");
                    }
                    else
                    {
                        var (found, threshold) = Analyzer.FindThresholdForTarget(
                            scores, interval, expectedMinutes * 60.0, adUnitSeconds, minBreakSeconds, black, silence, maxNudgeSeconds);
                        segments = found;
                        usedThresholdLabel = threshold.ToString("F3", CultureInfo.InvariantCulture);
                        thresholdCurve = Enumerable.Repeat(threshold, scores.Count).ToArray();
                        Log($"Settled on similarity threshold {threshold:F3}.");
                    }
                }
                else if (isAdaptive)
                {
                    segments = Analyzer.BuildSegmentsAdaptive(scores, interval, manualDip, localWindowSeconds, adUnitSeconds, minBreakSeconds);
                    usedThresholdLabel = $"local baseline − {manualDip:F3}";
                    var baseline = Analyzer.ComputeLocalBaseline(scores, interval, localWindowSeconds);
                    thresholdCurve = baseline.Select(b => b - manualDip).ToArray();

                    if (refineTransitions && black is not null && silence is not null)
                    {
                        var beforeCount = segments.Count(s => s.IsCommercial);
                        segments = Analyzer.ValidateBreaksAgainstBumpers(segments, black, silence);
                        var droppedCount = beforeCount - segments.Count(s => s.IsCommercial);
                        segments = Analyzer.RefineBoundariesWithBlackAndSilence(segments, black, silence, maxNudgeSeconds);
                        Log($"{droppedCount} uncorroborated break(s) dropped; remaining cuts nudged within {maxNudgeSeconds:F1}s.");
                    }
                }
                else
                {
                    segments = Analyzer.BuildSegments(scores, interval, manualThreshold, adUnitSeconds, minBreakSeconds);
                    usedThresholdLabel = manualThreshold.ToString("F3", CultureInfo.InvariantCulture);
                    thresholdCurve = Enumerable.Repeat(manualThreshold, scores.Count).ToArray();

                    if (refineTransitions && black is not null && silence is not null)
                    {
                        var beforeCount = segments.Count(s => s.IsCommercial);
                        segments = Analyzer.ValidateBreaksAgainstBumpers(segments, black, silence);
                        var droppedCount = beforeCount - segments.Count(s => s.IsCommercial);
                        segments = Analyzer.RefineBoundariesWithBlackAndSilence(segments, black, silence, maxNudgeSeconds);
                        Log($"{droppedCount} uncorroborated break(s) dropped; remaining cuts nudged within {maxNudgeSeconds:F1}s.");
                    }
                }

                if (refineTransitions && black is not null)
                {
                    // Some networks keep their bug visible through most of a commercial pod —
                    // logo-absence can't see that no matter the threshold, but the pod is still
                    // bracketed by cut-to-black bumpers. This catches those independently and
                    // folds them in wherever the logo path called it program.
                    var bridged = Analyzer.FindBlackBridgedBreaks(black);
                    var beforeBridgeCount = segments.Count(s => s.IsCommercial);
                    segments = Analyzer.MergeBlackBridgedBreaks(segments, bridged);
                    var addedCount = segments.Count(s => s.IsCommercial) - beforeBridgeCount;
                    if (addedCount > 0)
                        Log($"Found {addedCount} additional break(s) via black-bridge detection (bug stayed visible through them).");
                }

                if (trimEdgePromos && black is not null && silence is not null)
                {
                    var beforeCommercialCount = segments.Count(s => s.IsCommercial);
                    segments = Analyzer.TrimLeadingTrailingPromo(segments, black, silence, maxPromoSeconds, _durationSeconds);
                    if (segments.Count(s => s.IsCommercial) > beforeCommercialCount)
                        Log("Trimmed a leading/trailing network promo using black/silence dips.");
                }

                ConfigStore.SaveSegments(_segmentsPath, segments);

                Segments.Clear();
                foreach (var s in segments)
                {
                    var row = new SegmentRow(s);
                    row.PropertyChanged += (_, _) => RecomputeCutMap();
                    Segments.Add(row);
                }
                RecomputeCutMap();

                var commercialTotal = segments.Where(s => s.IsCommercial).Sum(s => s.DurationSeconds);
                var programTotal    = segments.Where(s => !s.IsCommercial).Sum(s => s.DurationSeconds);
                AnalyzeInfoText.Text = $"{segments.Count} segments — program {TimeSpan.FromSeconds(programTotal):hh\\:mm\\:ss}, " +
                                       $"commercial {TimeSpan.FromSeconds(commercialTotal):hh\\:mm\\:ss}";
                ThresholdInfoText.Text = $"Cutoff used: {usedThresholdLabel} " +
                                          "(curve below — green = above cutoff/program, red = below/commercial)";
                DrawSimilarityChart(scores, thresholdCurve, segments);
                Log("Analysis complete. Review the segment list below, then cut.");
                CutButton.IsEnabled = true;

                _lastScores = scores;
                _lastBlack = black;
                _lastSilence = silence;
                _lastSegments = segments;
                _lastThresholdCurve = thresholdCurve;
                _lastInterval = interval;
                ExportDiagnosticsButton.IsEnabled = true;
                SaveSettingsFromUi();
            }, "Cataloging...");
        }
        catch (OperationCanceledException)
        {
            AnalyzeInfoText.Text = "Analysis aborted.";
            Log("Analysis aborted.");
        }
        catch (Exception ex)
        {
            AnalyzeInfoText.Text = "Analysis failed.";
            Log($"Analysis failed: {ex.Message}");
        }
        finally
        {
            AnalyzeButton.IsEnabled = true;
        }
    }

    // Dumps the raw per-sample similarity scores, detected black/silent intervals, and final
    // segments to a CSV. Useful for sanity-checking the detection: open it next to a list of
    // break timestamps you identified by eye and see where the numbers agree or disagree.
    private void ExportDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        if (_lastScores is null || _lastSegments is null)
        {
            Log("Run analysis first.");
            return;
        }

        var dlg = new SaveFileDialog
        {
            Title = "Save diagnostics as",
            Filter = "CSV file|*.csv",
            FileName = "diagnostics.csv",
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            using var w = new StreamWriter(dlg.FileName, append: false);
            w.WriteLine("Type,StartSeconds,EndSeconds,Value");

            foreach (var s in _lastScores)
                w.WriteLine($"Sample,{s.TimeSeconds.ToString(CultureInfo.InvariantCulture)},,{s.Similarity.ToString(CultureInfo.InvariantCulture)}");

            if (_lastThresholdCurve is not null)
                for (int i = 0; i < _lastScores.Count && i < _lastThresholdCurve.Length; i++)
                    w.WriteLine($"Cutoff,{_lastScores[i].TimeSeconds.ToString(CultureInfo.InvariantCulture)},,{_lastThresholdCurve[i].ToString(CultureInfo.InvariantCulture)}");

            if (_lastBlack is not null)
                foreach (var b in _lastBlack)
                    w.WriteLine($"Black,{b.Start.ToString(CultureInfo.InvariantCulture)},{b.End.ToString(CultureInfo.InvariantCulture)},");

            if (_lastSilence is not null)
                foreach (var s in _lastSilence)
                    w.WriteLine($"Silence,{s.Start.ToString(CultureInfo.InvariantCulture)},{s.End.ToString(CultureInfo.InvariantCulture)},");

            foreach (var seg in _lastSegments)
                w.WriteLine($"Segment,{seg.StartSeconds.ToString(CultureInfo.InvariantCulture)},{seg.EndSeconds.ToString(CultureInfo.InvariantCulture)},{seg.IsCommercial}");

            Log($"Diagnostics saved to {dlg.FileName}");
        }
        catch (Exception ex)
        {
            Log($"Diagnostics export failed: {ex.Message}");
        }
    }

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { Title = "Save cleaned video as", Filter = "MP4 video|*.mp4" };
        if (!string.IsNullOrEmpty(OutputPathBox.Text)) dlg.FileName = OutputPathBox.Text;
        if (dlg.ShowDialog(this) == true) OutputPathBox.Text = dlg.FileName;
    }

    private void CutMode_Changed(object sender, RoutedEventArgs e)
    {
        if (CutModeInfoText is null) return; // fires during InitializeComponent before the field exists
        HybridToleranceBox.IsEnabled = HybridCutRadio.IsChecked == true;
        CutModeInfoText.Text = FastCutRadio.IsChecked == true
            ? "No re-encoding — every cut snaps to the nearest preceding keyframe. Fastest, but may keep a couple of seconds of adjacent commercial."
            : HybridCutRadio.IsChecked == true
                ? "Cuts within the tolerance snap to a keyframe (fast); cuts farther away are re-encoded precisely. Good middle ground."
                : "Every cut is re-encoded to land exactly on the analyzed boundary. Slowest, frame-accurate.";
    }

    private async void Cut_Click(object sender, RoutedEventArgs e)
    {
        if (_videoPath is null) return;
        var outputPath = OutputPathBox.Text;
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            Log("Choose an output file path first.");
            return;
        }

        double hybridTolerance = 0;
        if (HybridCutRadio.IsChecked == true &&
            !double.TryParse(HybridToleranceBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out hybridTolerance))
        {
            Log("Enter a valid snap tolerance (seconds) for hybrid cut.");
            return;
        }

        CutButton.IsEnabled = false;
        try
        {
            await RunWithProgressAsync(async (progress, ct) =>
            {
                var segments = Segments.Select(r => r.ToSegment()).ToList();
                await RunSelectedCutModeAsync(_videoPath, segments, outputPath, hybridTolerance, progress, ct);
            }, "Cutting...");

            Log($"Done. Saved to {outputPath}");
            Log($"Full ffmpeg log: {outputPath}.ffmpeg.log");
            MessageBox.Show(this, $"Saved: {outputPath}", "Commercial Cutter", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            Log("Cut aborted.");
        }
        catch (Exception ex)
        {
            Log($"Cut failed: {ex.Message}");
        }
        finally
        {
            CutButton.IsEnabled = true;
        }
    }

    // Runs whichever cut mode is currently selected in the radio group. Shared by the
    // single-file Cut button and the batch pipeline so they can't drift out of sync.
    private async Task RunSelectedCutModeAsync(
        string videoPath, List<Segment> segments, string outputPath, double hybridTolerance,
        IProgress<double>? progress, CancellationToken ct)
    {
        if (FastCutRadio.IsChecked == true)
        {
            Log("Cutting — stream copy, snapping to keyframes...");
            var keyframes = await Ffmpeg.GetKeyframeTimestampsAsync(videoPath, ct);
            await Cutter.FastCutAsync(videoPath, segments, keyframes, outputPath, progress, ct);
        }
        else if (HybridCutRadio.IsChecked == true)
        {
            Log($"Cutting — hybrid, snapping cuts within {hybridTolerance:F1}s of a keyframe...");
            var keyframes = await Ffmpeg.GetKeyframeTimestampsAsync(videoPath, ct);
            var (copied, reencoded) = await Cutter.HybridCutAsync(
                videoPath, segments, keyframes, hybridTolerance, _nvencAvailable, outputPath, progress, ct);
            Log($"{copied} segment(s) stream-copied, {reencoded} re-encoded.");
        }
        else
        {
            Log($"Cutting — re-encoding with {(_nvencAvailable ? "NVENC" : "libx264")}, this can take a while...");
            await Cutter.CutAsync(videoPath, segments, outputPath, _nvencAvailable, progress, ct);
        }
    }

    private static readonly string[] VideoExtensions = { ".mp4", ".mkv", ".ts", ".mts", ".m2ts", ".avi" };

    public ObservableCollection<BatchFileRow> BatchFiles { get; } = new();

    private void BrowseBatchFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Choose a folder of recordings to process",
            InitialDirectory = _settings.LastBatchFolder ?? "",
        };
        if (dlg.ShowDialog(this) != true) return;

        BatchFolderBox.Text = dlg.FolderName;
        BatchFiles.Clear();
        BatchGrid.ItemsSource = BatchFiles;

        var files = Directory.EnumerateFiles(dlg.FolderName, "*.*", SearchOption.AllDirectories)
            .Where(f => VideoExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);

        foreach (var f in files)
            BatchFiles.Add(new BatchFileRow(f, Path.GetRelativePath(dlg.FolderName, f)));

        BatchInfoText.Text = $"{BatchFiles.Count} video file(s) found.";
        StartBatchButton.IsEnabled = BatchFiles.Count > 0 && _configPath is not null;
    }

    private async void StartBatch_Click(object sender, RoutedEventArgs e)
    {
        if (_configPath is null || !File.Exists(_configPath))
        {
            Log("Mark the logo box (step 2) on a sample episode before running a batch.");
            return;
        }
        if (!double.TryParse(IntervalBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var interval) || interval <= 0)
        {
            Log("Enter a valid sample interval (step 3).");
            return;
        }

        var isAdaptive = AdaptiveModeRadio.IsChecked == true;
        var manualDip = 0.0;
        var manualThreshold = 0.0;
        var hasManualThreshold = isAdaptive
            ? double.TryParse(AdaptiveDipBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out manualDip)
            : double.TryParse(ThresholdBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out manualThreshold);
        var hasExpectedLength  = double.TryParse(ExpectedLengthBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var expectedMinutes);
        if (!hasManualThreshold && !hasExpectedLength)
        {
            Log(isAdaptive
                ? "Enter either an expected program length or a manual max-dip value (step 3)."
                : "Enter either an expected program length or a manual similarity threshold (step 3).");
            return;
        }
        var localWindowSeconds = 300.0;
        if (isAdaptive &&
            (!double.TryParse(LocalWindowSecondsBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out localWindowSeconds) || localWindowSeconds <= 0))
        {
            Log("Enter a valid local window radius (step 3).");
            return;
        }
        if (!double.TryParse(AdUnitSecondsBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var adUnitSeconds) || adUnitSeconds <= 0)
        {
            Log("Enter a valid ad unit length (step 3).");
            return;
        }
        if (!double.TryParse(MinBreakSecondsBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var minBreakSeconds) || minBreakSeconds < 0)
        {
            Log("Enter a valid minimum break length (step 3).");
            return;
        }
        var refineTransitions = RefineTransitionsCheckBox.IsChecked == true;
        var maxNudgeSeconds = 0.0;
        if (refineTransitions &&
            (!double.TryParse(MaxNudgeSecondsBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out maxNudgeSeconds) || maxNudgeSeconds < 0))
        {
            Log("Enter a valid max nudge (seconds) for transition refinement.");
            return;
        }
        var trimEdgePromos = TrimEdgePromosCheckBox.IsChecked == true;
        var maxPromoSeconds = 90.0;
        if (trimEdgePromos &&
            (!double.TryParse(MaxPromoSecondsBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out maxPromoSeconds) || maxPromoSeconds <= 0))
        {
            Log("Enter a valid max promo length (seconds).");
            return;
        }

        double hybridTolerance = 0;
        if (HybridCutRadio.IsChecked == true &&
            !double.TryParse(HybridToleranceBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out hybridTolerance))
        {
            Log("Enter a valid snap tolerance (seconds) for hybrid cut.");
            return;
        }

        StartBatchButton.IsEnabled = false;
        var config = ConfigStore.LoadConfig(_configPath);
        int done = 0, failed = 0;

        try
        {
            await RunWithProgressAsync(async (progress, ct) =>
            {
                foreach (var row in BatchFiles)
                {
                    ct.ThrowIfCancellationRequested();
                    BatchInfoText.Text = $"Processing {done + failed + 1} of {BatchFiles.Count}: {row.RelativePath}";

                    try
                    {
                        row.Status = "Cataloging";
                        var duration = await Ffmpeg.GetDurationSecondsAsync(row.FullPath, ct);
                        var workDir   = LocalPaths.GetWorkDir(row.FullPath);
                        var thumbsDir = Path.Combine(workDir, "thumbs");
                        await Cataloger.CatalogAsync(row.FullPath, config.Crop, thumbsDir, interval, duration, progress, ct);

                        List<(double Start, double End)>? black = null, silence = null;
                        if (refineTransitions || trimEdgePromos)
                        {
                            row.Status = "Scanning transitions";
                            (black, silence) = await Ffmpeg.DetectBlackAndSilenceAsync(row.FullPath, duration, progress, ct);
                        }

                        row.Status = "Analyzing";
                        var scores = await Task.Run(() => Analyzer.ScoreThumbnails(thumbsDir, config.ReferenceImagePath, interval), ct);
                        List<Segment> segments;
                        if (hasExpectedLength)
                        {
                            segments = isAdaptive
                                ? Analyzer.FindAdaptiveThresholdForTarget(scores, interval, expectedMinutes * 60.0, localWindowSeconds, adUnitSeconds, minBreakSeconds, black, silence, maxNudgeSeconds).Segments
                                : Analyzer.FindThresholdForTarget(scores, interval, expectedMinutes * 60.0, adUnitSeconds, minBreakSeconds, black, silence, maxNudgeSeconds).Segments;
                        }
                        else
                        {
                            segments = isAdaptive
                                ? Analyzer.BuildSegmentsAdaptive(scores, interval, manualDip, localWindowSeconds, adUnitSeconds, minBreakSeconds)
                                : Analyzer.BuildSegments(scores, interval, manualThreshold, adUnitSeconds, minBreakSeconds);
                        }

                        if (refineTransitions && black is not null && silence is not null && !hasExpectedLength)
                        {
                            segments = Analyzer.ValidateBreaksAgainstBumpers(segments, black, silence);
                            segments = Analyzer.RefineBoundariesWithBlackAndSilence(segments, black, silence, maxNudgeSeconds);
                        }

                        if (refineTransitions && black is not null)
                        {
                            var bridged = Analyzer.FindBlackBridgedBreaks(black);
                            segments = Analyzer.MergeBlackBridgedBreaks(segments, bridged);
                        }

                        if (trimEdgePromos && black is not null && silence is not null)
                            segments = Analyzer.TrimLeadingTrailingPromo(segments, black, silence, maxPromoSeconds, duration);

                        // Batch mode keeps the original filename in place rather than producing a
                        // separate "_clean" copy: cut to a temp file first (ffmpeg is still reading
                        // the original as input, so it can't be the output path), then once that
                        // succeeds, move the original into an "originals" subfolder next to it and
                        // drop the cut file in under the original name.
                        row.Status = "Cutting";
                        var sourceDir = Path.GetDirectoryName(row.FullPath)!;
                        var tempOutputPath = Path.Combine(LocalPaths.GetWorkDir(row.FullPath), "batch_cut.mp4");
                        await RunSelectedCutModeAsync(row.FullPath, segments, tempOutputPath, hybridTolerance, progress, ct);

                        var originalsDir = Path.Combine(sourceDir, "originals");
                        Directory.CreateDirectory(originalsDir);
                        var originalDest = Path.Combine(originalsDir, Path.GetFileName(row.FullPath));
                        if (File.Exists(originalDest)) File.Delete(originalDest);
                        File.Move(row.FullPath, originalDest);
                        File.Move(tempOutputPath, row.FullPath);

                        row.Status = "Done";
                        done++;
                    }
                    catch (OperationCanceledException)
                    {
                        row.Status = "Aborted";
                        throw;
                    }
                    catch (Exception ex)
                    {
                        row.Status = $"Error: {ex.Message}";
                        failed++;
                        Log($"{row.RelativePath} failed: {ex.Message}");
                    }
                }
            }, "Batch processing...");
        }
        catch (OperationCanceledException)
        {
            Log("Batch aborted.");
        }
        finally
        {
            BatchInfoText.Text = $"Batch complete — {done} done, {failed} failed, {BatchFiles.Count - done - failed} not reached.";
            StartBatchButton.IsEnabled = true;
        }
    }
}
