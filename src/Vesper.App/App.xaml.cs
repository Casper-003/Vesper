using Microsoft.UI.Xaml;

namespace Vesper.App;

public partial class App : Application
{
    private Window? window;
    public App() => InitializeComponent();
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (Environment.GetEnvironmentVariable("VESPER_MONITORING_INIT_CHECK_PATH") is { Length: > 0 } initializationReport)
        {
            window = new MonitoringCheckWindow(initializationReport, initialize: true);
            window.Activate();
            return;
        }
        if (Environment.GetEnvironmentVariable("VESPER_MONITORING_CHECK_PATH") is { Length: > 0 } monitoringReport)
        {
            window = new MonitoringCheckWindow(monitoringReport);
            window.Activate();
            return;
        }
        if (Environment.GetEnvironmentVariable("VESPER_HOME_CHECK_PATH") is { Length: > 0 } homeReport)
        {
            window = new HomeCheckWindow(homeReport);
            window.Activate();
            return;
        }
        if (Environment.GetEnvironmentVariable("VESPER_SINGING_CHECK_PATH") is { Length: > 0 } singingReport)
        {
            window = new SingingCheckWindow(singingReport);
            window.Activate();
            return;
        }
        if (Environment.GetEnvironmentVariable("VESPER_PREVIEW_CHECK_PATH") is { Length: > 0 } previewReport)
        {
            window = new PreviewCheckWindow(previewReport);
            window.Activate();
            return;
        }
        if (Environment.GetEnvironmentVariable("VESPER_PLAYBACK_CHECK_PATH") is { Length: > 0 } report)
        {
            window = new PlaybackCheckWindow(report);
            window.Activate();
            return;
        }
        window = new MainWindow();
        window.Activate();
    }
}
