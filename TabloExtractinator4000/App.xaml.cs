using Microsoft.Extensions.DependencyInjection;
using System.Net.Http;
using System.Windows;
using Application = System.Windows.Application;
using TabloExtractinator4000.Services;
using TabloExtractinator4000.ViewModels;

namespace TabloExtractinator4000;

public partial class App : Application
{
    private readonly ServiceProvider _services;

    public App()
    {
        var sc = new ServiceCollection();

        // HttpClient — single shared instance for the app lifetime
        sc.AddSingleton<HttpClient>();

        // Services
        sc.AddSingleton<TabloAuthService>();
        sc.AddSingleton<TabloApiService>();
        sc.AddSingleton<FfmpegService>();
        sc.AddSingleton<FilenameService>();
        sc.AddSingleton<AuditLogService>();
        sc.AddSingleton<SettingsService>();
        sc.AddSingleton<ExportOrchestrator>();

        // ViewModels + Window
        sc.AddSingleton<MainViewModel>();
        sc.AddSingleton<GuideViewModel>();
        sc.AddSingleton<MainWindow>();

        _services = sc.BuildServiceProvider();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var window = _services.GetRequiredService<MainWindow>();
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services.Dispose();
        base.OnExit(e);
    }
}
