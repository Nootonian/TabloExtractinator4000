using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using TabloExtractinator4000.Models;

namespace TabloExtractinator4000.Views;

public partial class GuideAiringDetailWindow : Window
{
    [DllImport("dwmapi.dll", PreserveSig = false)]
    static extern void DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        int dark = 1;
        try { DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int)); } catch { }
        try { DwmSetWindowAttribute(hwnd, 19, ref dark, sizeof(int)); } catch { }
    }

    public bool UserWantsToSchedule   { get; private set; }
    public bool UserWantsToPlay       { get; private set; }
    public bool UserWantsToUnschedule { get; private set; }

    public GuideAiringDetailWindow(GuideAiring airing, bool isOnNow, bool allowSchedule, bool alreadyScheduled = false)
    {
        InitializeComponent();

        ShowTitleText.Text    = airing.ShowTitle;
        EpisodeTitleText.Text = airing.EpisodeTitle;
        EpisodeTitleText.Visibility = string.IsNullOrEmpty(airing.EpisodeTitle)
            ? Visibility.Collapsed : Visibility.Visible;

        var local = airing.StartsAt.LocalDateTime;
        var end   = airing.EndsAt.LocalDateTime;
        var dur   = TimeSpan.FromSeconds(airing.DurationSeconds);
        TimeText.Text = $"{local:dddd, MMMM d  h:mm tt} – {end:h:mm tt}  ({(int)dur.TotalMinutes} min)";

        DescriptionText.Text = airing.Description;
        DescriptionText.Visibility = string.IsNullOrEmpty(airing.Description)
            ? Visibility.Collapsed : Visibility.Visible;

        // Status badges — alreadyScheduled takes priority over on-now
        if (alreadyScheduled)
            ScheduledBadge.Visibility = Visibility.Visible;
        else if (isOnNow)
            OnNowBadge.Visibility = Visibility.Visible;

        // Watch Live — only for currently-airing programs
        WatchButton.Visibility = isOnNow ? Visibility.Visible : Visibility.Collapsed;

        // Cancel This Recording — only when already scheduled
        UnscheduleButton.Visibility = alreadyScheduled ? Visibility.Visible : Visibility.Collapsed;

        // Schedule Recording — only when future and not yet scheduled
        ScheduleButton.Visibility = allowSchedule ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Watch_Click(object sender, RoutedEventArgs e)
    {
        UserWantsToPlay = true;
        DialogResult    = true;
    }

    private void Unschedule_Click(object sender, RoutedEventArgs e)
    {
        UserWantsToUnschedule = true;
        DialogResult          = true;
    }

    private void Schedule_Click(object sender, RoutedEventArgs e)
    {
        UserWantsToSchedule = true;
        DialogResult        = true;
    }
}
