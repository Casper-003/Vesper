using System.Collections.Specialized;
using System.ComponentModel;

namespace Vesper.App;

public sealed partial class MainWindow
{
    private void InitializeHome()
    {
        HomeSections.Bind(monitoring);
        HomeSections.StartSingingRequested += OnEnterSinging;
        HomePage.Loaded += (_, _) =>
        {
            UpdateHomeConsoleActiveState();
            RefreshHomeConsoleQueue();
        };
        HomePage.Unloaded += (_, _) => UpdateHomeConsoleActiveState();
        viewModel.Queue.PropertyChanged += OnHomeQueueChanged;
        viewModel.Queue.Pending.CollectionChanged += OnHomeQueuePendingChanged;
        Closed += (_, _) =>
        {
            HomeSections.SetActive(false);
            HomeSections.Unbind();
            viewModel.Queue.PropertyChanged -= OnHomeQueueChanged;
            viewModel.Queue.Pending.CollectionChanged -= OnHomeQueuePendingChanged;
        };
        RefreshHomeConsoleQueue();
        UpdateHomeConsoleActiveState();
    }

    private void OnHomeQueueChanged(object? sender, PropertyChangedEventArgs e) => RefreshHomeConsoleQueue();

    private void OnHomeQueuePendingChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshHomeConsoleQueue();

    private void RefreshHomeConsoleQueue()
    {
        if (closed) return;
        HomeSections.UpdateQueueStatus(
            viewModel.Queue.Pending.Count,
            viewModel.Queue.Current?.Title,
            singing?.Loading == true);
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
