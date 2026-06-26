using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace CommercialCutter;

public partial class BoxSelectWindow : Window
{
    private readonly string _videoPath;
    private readonly string _frameDir;
    private readonly double _durationSeconds;
    private double _currentSeconds;
    private BitmapImage _bitmap = null!;
    private Point _dragStart;
    private bool  _dragging;
    private bool  _seeking;

    public CropRect? Result { get; private set; }

    public BoxSelectWindow(string videoPath, string initialFramePath, double initialSeconds, double durationSeconds)
    {
        InitializeComponent();

        _videoPath = videoPath;
        _frameDir = Path.GetDirectoryName(Path.GetFullPath(initialFramePath))!;
        _durationSeconds = durationSeconds;
        _currentSeconds = initialSeconds;

        LoadFrame(initialFramePath);
        UpdateTimeText();
    }

    // BitmapImage caches decoded frames by URI, so re-extracting a new frame to the same
    // filename and loading it again would otherwise just show the stale cached image —
    // IgnoreImageCache forces a real re-read, and a fresh BitmapImage instance each call
    // avoids any chance of the old one's internal state lingering.
    private void LoadFrame(string framePath)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        bitmap.UriSource = new Uri(Path.GetFullPath(framePath));
        bitmap.EndInit();

        _bitmap = bitmap;
        FrameImage.Source = _bitmap;
        SelectionRect.Visibility = Visibility.Collapsed;
        ConfirmButton.IsEnabled = false;
    }

    private void UpdateTimeText() =>
        CurrentTimeText.Text = TimeSpan.FromSeconds(_currentSeconds).ToString(@"hh\:mm\:ss");

    private async void Seek_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tagStr } || !double.TryParse(tagStr, CultureInfo.InvariantCulture, out var delta)) return;
        await SeekToAsync(_currentSeconds + delta);
    }

    private async void GoTo_Click(object sender, RoutedEventArgs e)
    {
        if (double.TryParse(GoToBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            await SeekToAsync(seconds);
        else
            StatusText.Text = "Enter a valid number of seconds.";
    }

    private async System.Threading.Tasks.Task SeekToAsync(double seconds)
    {
        if (_seeking) return;
        _seeking = true;
        SetNavEnabled(false);
        StatusText.Text = "Extracting frame...";

        try
        {
            _currentSeconds = Math.Clamp(seconds, 0, Math.Max(0, _durationSeconds));
            var framePath = Path.Combine(_frameDir, $"selectbox_frame_{DateTime.Now.Ticks}.jpg");
            await Cataloger.ExtractSingleFrameAsync(_videoPath, _currentSeconds, framePath);
            LoadFrame(framePath);
            UpdateTimeText();
            StatusText.Text = "Drag a box around the logo bug.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Frame extraction failed: {ex.Message}";
        }
        finally
        {
            SetNavEnabled(true);
            _seeking = false;
        }
    }

    private void SetNavEnabled(bool enabled)
    {
        foreach (var child in SeekPanel.Children)
            if (child is Control c) c.IsEnabled = enabled;
    }

    private void ImageHost_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(Overlay);
        _dragging  = true;
        SelectionRect.Visibility = Visibility.Visible;
        Canvas.SetLeft(SelectionRect, _dragStart.X);
        Canvas.SetTop(SelectionRect, _dragStart.Y);
        SelectionRect.Width  = 0;
        SelectionRect.Height = 0;
    }

    private void ImageHost_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;

        var pos = e.GetPosition(Overlay);
        var x = Math.Min(pos.X, _dragStart.X);
        var y = Math.Min(pos.Y, _dragStart.Y);
        var w = Math.Abs(pos.X - _dragStart.X);
        var h = Math.Abs(pos.Y - _dragStart.Y);

        Canvas.SetLeft(SelectionRect, x);
        Canvas.SetTop(SelectionRect, y);
        SelectionRect.Width  = w;
        SelectionRect.Height = h;
    }

    private void ImageHost_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _dragging = false;
        ConfirmButton.IsEnabled = SelectionRect.Width > 4 && SelectionRect.Height > 4;
        StatusText.Text = ConfirmButton.IsEnabled
            ? "Box selected. Click Confirm to use it, or drag again to redo."
            : "Box too small — drag a larger rectangle.";
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        // FrameImage is Stretch=Uniform inside ImageHost; figure out the letterboxed
        // rendered rect so we can map canvas (control) coordinates back to source pixels.
        var hostW = ImageHost.ActualWidth;
        var hostH = ImageHost.ActualHeight;
        var srcW  = (double)_bitmap.PixelWidth;
        var srcH  = (double)_bitmap.PixelHeight;

        var scale   = Math.Min(hostW / srcW, hostH / srcH);
        var renderW = srcW * scale;
        var renderH = srcH * scale;
        var offsetX = (hostW - renderW) / 2.0;
        var offsetY = (hostH - renderH) / 2.0;

        var left = Canvas.GetLeft(SelectionRect);
        var top  = Canvas.GetTop(SelectionRect);

        var px = (left - offsetX) / scale;
        var py = (top  - offsetY) / scale;
        var pw = SelectionRect.Width  / scale;
        var ph = SelectionRect.Height / scale;

        px = Math.Clamp(px, 0, srcW);
        py = Math.Clamp(py, 0, srcH);
        pw = Math.Clamp(pw, 1, srcW - px);
        ph = Math.Clamp(ph, 1, srcH - py);

        Result = new CropRect((int)px, (int)py, (int)pw, (int)ph);
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
