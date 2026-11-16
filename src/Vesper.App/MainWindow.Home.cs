namespace Vesper.App;

public sealed partial class MainWindow
{
    private void InitializeHome()
    {
        HomeSections.Bind(monitoring);
        HomeSections.StartSingingRequested += OnEnterSinging;
        HomePage.Loaded += (_, _) => UpdateHomeConsoleActiveState();
        HomePage.Unloaded += (_, _) => UpdateHomeConsoleActiveState();
        Closed += (_, _) =>
        {
            HomeSections.SetActive(false);
            HomeSections.Unbind();
        };
        UpdateHomeConsoleActiveState();
    }

    private void UpdateHomeConsoleActiveState()
    {
        bool active = !closed
            && !IsIsolatedUiCheck()
            && !singingVisible
            && HomePage.IsLoaded
            && (ContentFrame.Content as ShellPage)?.PageKey == "首页";
        HomeSections.SetActive(active);
    }

    private static Task PrepareHomeResourcesAsync(CancellationToken token) => Task.CompletedTask;
}
