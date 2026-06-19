using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using TabloExtractinator4000.Services;
using TabloExtractinator4000.ViewModels;

namespace TabloExtractinator4000;

public partial class MainWindow : Window
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

    public MainWindow(MainViewModel vm, GuideViewModel guideVm)
    {
        InitializeComponent();
        DataContext           = vm;
        GuideTab.DataContext  = guideVm;
        // Defer the scroll until after WPF has finished layout — otherwise
        // ScrollableWidth is still 0 and ScrollToHorizontalOffset is a no-op.
        guideVm.ScrollToRequested += offset =>
            Dispatcher.InvokeAsync(
                () => GuideBodyScroll?.ScrollToHorizontalOffset(offset),
                System.Windows.Threading.DispatcherPriority.Loaded);

        RecordingsGrid.SizeChanged += (_, _) => UpdateEpisodeColumnWidth();

        Loaded += async (_, _) =>
        {
            PasswordBox.Password = vm.Password;
            UpdateEpisodeColumnWidth();
            await vm.AutoStartAsync();
        };
    }

    // Episode column (index 2) must fill available space. WPF star columns break in grouped
    // DataGrids — the GroupItem intercepts available-width measurement. We compute it manually.
    private void UpdateEpisodeColumnWidth()
    {
        if (RecordingsGrid.ActualWidth < 50 || RecordingsGrid.Columns.Count < 3) return;
        // Sum of spacer(28) + checkbox(24) + S/E(62) + Aired(88) + Duration(62)
        //   + Size(72) + Network(72) + Ch(40) + State(66) = 514, plus scrollbar ~18
        const double otherCols = 514 + 18;
        var episode = RecordingsGrid.ActualWidth - otherCols;
        RecordingsGrid.Columns[2].Width = new DataGridLength(Math.Max(120, episode));
    }

    private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.Password = ((PasswordBox)sender).Password;
    }

    private void Recording_SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.OnSelectionChanged();
    }

    private bool _guideSyncLock;

    private void GuideBodyScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_guideSyncLock) return;
        _guideSyncLock = true;
        try
        {
            if (GuideDayScroll    != null) GuideDayScroll.ScrollToHorizontalOffset(e.HorizontalOffset);
            if (GuideHeaderScroll != null) GuideHeaderScroll.ScrollToHorizontalOffset(e.HorizontalOffset);
            if (GuideChannelScroll != null) GuideChannelScroll.ScrollToVerticalOffset(e.VerticalOffset);
        }
        finally { _guideSyncLock = false; }
    }

    private void GuideChannelScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_guideSyncLock) return;
        _guideSyncLock = true;
        try
        {
            if (GuideBodyScroll != null) GuideBodyScroll.ScrollToVerticalOffset(e.VerticalOffset);
        }
        finally { _guideSyncLock = false; }
    }

    // ── Guide tab helpers ──────────────────────────────────────────────────

    private GuideViewModel? GuideVm => GuideTab.DataContext as GuideViewModel;

    private void MainTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MainTabControl.SelectedItem == GuideTab && GuideVm is { } gvm
            && !gvm.IsLoading && gvm.Rows.Count == 0)
            _ = gvm.LoadGuideCommand.ExecuteAsync(null);
    }

    // Track last click time per element for manual double-click detection
    private (object? sender, DateTime at) _lastClick;

    private bool IsDoubleClick(object sender)
    {
        var now = DateTime.UtcNow;
        if (_lastClick.sender == sender && (now - _lastClick.at).TotalMilliseconds < 400)
        {
            _lastClick = default;
            return true;
        }
        _lastClick = (sender, now);
        return false;
    }

    private void AiringCell_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount < 2) return;
        if (sender is FrameworkElement fe && fe.DataContext is GuideAiringCell cell && GuideVm is { } gvm)
        {
            e.Handled = true;
            _ = gvm.AiringDoubleClickAsync(cell);
        }
    }

    private void ChannelLabel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount < 2) return;
        if (sender is FrameworkElement fe && fe.DataContext is GuideChannelRow row && GuideVm is { } gvm)
        {
            e.Handled = true;
            _ = gvm.ChannelDoubleClickAsync(row);
        }
    }

    // Saved in Opened so click handlers don't have to navigate the visual tree
    private GuideAiringCell? _contextMenuCell;

    private void AiringContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.ContextMenu menu) return;
        if (menu.PlacementTarget is not FrameworkElement fe) return;
        if (fe.DataContext is not GuideAiringCell cell) return;

        _contextMenuCell = cell;

        var items = menu.Items.OfType<System.Windows.Controls.MenuItem>();
        foreach (var mi in items)
        {
            mi.Visibility = mi.Name switch
            {
                "MenuWatchLive"   => cell.IsCurrentlyAiring               ? Visibility.Visible : Visibility.Collapsed,
                "MenuSchedule"    => cell.IsFuture && !cell.IsScheduled    ? Visibility.Visible : Visibility.Collapsed,
                "MenuUnschedule"  => cell.IsScheduled                      ? Visibility.Visible : Visibility.Collapsed,
                _                 => Visibility.Visible
            };
        }
    }

    private void AiringWatchLive_Click(object sender, RoutedEventArgs e)
    {
        if (_contextMenuCell is { } cell && GuideVm is { } gvm)
            _ = gvm.PlayCurrentAiringCommand.ExecuteAsync(cell);
    }

    private void AiringSchedule_Click(object sender, RoutedEventArgs e)
    {
        Services.Logger.Log($"AiringSchedule_Click: _contextMenuCell={((_contextMenuCell == null) ? "null" : _contextMenuCell.Airing.ShowTitle)}");
        if (_contextMenuCell is { } cell && GuideVm is { } gvm)
            _ = gvm.ScheduleRecordingCommand.ExecuteAsync(cell);
    }

    private void AiringUnschedule_Click(object sender, RoutedEventArgs e)
    {
        if (_contextMenuCell is { } cell && GuideVm is { } gvm)
            _ = gvm.UnscheduleRecordingCommand.ExecuteAsync(cell);
    }

    private void AiringDetails_Click(object sender, RoutedEventArgs e)
    {
        if (_contextMenuCell is { } cell && GuideVm is { } gvm)
            gvm.ShowAiringDetailsCommand.Execute(cell);
    }

    private void ChannelWatchLive_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.MenuItem mi
            && mi.CommandParameter is GuideChannelRow row && GuideVm is { } gvm)
            _ = gvm.PlayLiveChannelCommand.ExecuteAsync(row);
    }

    private void AboutButton_Click(object sender, RoutedEventArgs e)
    {
        new Views.AboutWindow(MainViewModel.AppVersion) { Owner = this }.ShowDialog();
    }

    private void GroupCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.CheckBox cb) return;
        if (cb.Tag is not CollectionViewGroup group) return;
        if (DataContext is not MainViewModel vm) return;

        var select = cb.IsChecked == true;
        foreach (var item in group.Items.OfType<RecordingRowViewModel>())
            item.IsSelected = select;

        vm.OnSelectionChanged();
    }
}
