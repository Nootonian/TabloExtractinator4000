using System.Windows;

namespace TabloExtractinator4000.Views;

public partial class AboutWindow : Window
{
    public AboutWindow(string version)
    {
        InitializeComponent();
        VersionText.Text = $"Version {version}  ·  {System.DateTime.Now:MMMM yyyy}";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
