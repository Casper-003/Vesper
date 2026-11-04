using Microsoft.UI.Xaml;

namespace Vesper.App;

public partial class App : Application
{
    private Window? window;
    public App() => InitializeComponent();
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
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
