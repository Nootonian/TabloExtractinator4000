using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace TabloExtractinator4000.Views;

public partial class DetailsWindow : Window
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

    public string Details { get; }

    public DetailsWindow(string title, string details)
    {
        InitializeComponent();
        Title   = title;
        Details = details;
        DataContext = this;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
