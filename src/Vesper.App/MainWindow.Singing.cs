using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Vesper.Core;
using Windows.Foundation;

namespace Vesper.App;

public sealed partial class MainWindow
{
    private SingingSession singing = null!;
    private SingingView singingView = null!;
    private readonly DispatcherTimer singingClock = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private bool singingVisible, singingEntryRequested, singingExitPrompt;
    private int singingPresentationRevision;
    private AppWindowPresenter? browserPresenter;
    private Rect singingEntryBounds;

    private void InitializeSinging(ShellPreferences preferences)
    {
        singingView = new() { Visibility = Visibility.Collapsed };
        Grid.SetRowSpan(singingView, 2); Root.Children.Add(singingView);
        singing = new(DispatcherQueue, viewModel.Queue, () => viewModel.Library.Songs.ToArray(), PresentPreparedSingingAsync);
        singing.Playback.Attach(singingView.Video);
        SeekDuringSingingSwitch.IsOn = preferences.SingingSeekEnabled;
        singing.Changed += RefreshSinging;
        viewModel.Queue.PropertyChanged += OnSingingQueueChanged;
        viewModel.Queue.Pending.CollectionChanged += OnSingingPendingChanged;
        capsule.BeginSinging += OnEnterSinging;
        singingView.StartRequested += OnEnterSinging;
        singingView.ReturnRequested += () => { if (!singingExitPrompt) ReturnToBrowsing(); };
        singingView.PauseRequested += singing.TogglePause;
        singingView.NextRequested += OnSkipSinging;
        singingView.ExitRequested += OnExitSinging;
        singingView.SeekRequested += value => { if (SeekDuringSingingSwitch.IsOn) singing.Seek(value); };
        singingClock.Tick += (_, _) =>
        {
            if (singingVisible) singingView.UpdateTime(singing.Playback.Position, singing.Playback.Duration);
        };
        singingClock.Start();
        Closed += (_, _) =>
        {
            singingPresentationRevision++; singingClock.Stop(); singing.Dispose(); singingView.Dispose();
            viewModel.Queue.PropertyChanged -= OnSingingQueueChanged;
            viewModel.Queue.Pending.CollectionChanged -= OnSingingPendingChanged;
        };
        RefreshSinging();
    }

    private async void OnEnterSinging()
    {
        if (closed || singingExitPrompt || singingEntryRequested) return;
        singingEntryBounds = capsule.IsOpen ? capsule.SingingBounds
            : new Rect(Root.ActualWidth / 2 - 100, 0, 200, 48);
        singingEntryRequested = true;
        await monitoring.ReleaseForPlaybackAsync();
        if (closed) return;
        capsule.Close();
        if (singing.Ready && !singing.Loading)
        {
            ShowSinging(); singingEntryRequested = false;
            singingView.UpdateTime(singing.Playback.Position, singing.Playback.Duration);
        }
        else if (!singing.Loading)
        {
            if (viewModel.Queue.Pending.Count == 0)
            { ShowSinging(); singingEntryRequested = false; }
            else await singing.NextAsync();
        }
    }

    private async Task PresentPreparedSingingAsync()
    {
        if (closed) return;
        bool expanding = singingEntryRequested && !singingVisible;
        if (singingEntryRequested) ShowSinging();
        singingEntryRequested = false;
        if (!singingVisible) return; // Automatic next while browsing keeps that page.
        int request = singingPresentationRevision;
        if (expanding) singingView.PrepareExpansion(singingEntryBounds, new Size(Root.ActualWidth, Root.ActualHeight));
        try
        {
            await singingView.PresentFrameAsync(singing.FirstFrame);
            if (closed || !singingVisible || request != singingPresentationRevision) return;
            if (expanding) await singingView.ExpandAsync(singingEntryBounds, new Size(Root.ActualWidth, Root.ActualHeight));
        }
        finally { if (request == singingPresentationRevision) singingView.ResetTransition(); }
    }

    private void ShowSinging()
    {
        if (singingVisible || closed) return;
        singingVisible = true; singingPresentationRevision++;
        browserPresenter = AppWindow.Presenter;
        AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        capsule.Close(); capsule.Bar.Visibility = Visibility.Collapsed;
        Navigation.IsEnabled = false; BackButton.IsEnabled = ForwardButton.IsEnabled = false;
        singingView.Visibility = Visibility.Visible;
        Root.UpdateLayout(); singingView.SetActive(true); RefreshSinging();
    }

    private void ReturnToBrowsing()
    {
        singingEntryRequested = false; singingPresentationRevision++;
        if (!singingVisible || closed) return;
        singingVisible = false; singingView.SetActive(false); singingView.ResetTransition();
        singingView.Visibility = Visibility.Collapsed;
        if (browserPresenter is not null) AppWindow.SetPresenter(browserPresenter);
        browserPresenter = null;
        Navigation.IsEnabled = true; BackButton.IsEnabled = ContentFrame.CanGoBack; ForwardButton.IsEnabled = ContentFrame.CanGoForward;
        capsule.Bar.Visibility = Visibility.Visible;
        capsule.Bar.Focus(FocusState.Programmatic);
        DispatcherQueue.TryEnqueue(UpdateTitleLayout);
        // Keep the same player and video surface attached: no pause, prepare or seek.
    }

    private async void OnSkipSinging()
    {
        if (!singing.Loading && !singingExitPrompt) await singing.NextAsync();
    }

    private async void OnExitSinging()
    {
        if (singingExitPrompt || closed) return;
        singingExitPrompt = true;
        try
        {
            if (viewModel.Queue.Current is not null)
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = Root.XamlRoot, RequestedTheme = ElementTheme.Dark,
                    Title = "退出本场？", Content = "将停止当前歌曲，下次从头播放；待播队列保留。",
                    PrimaryButtonText = "退出本场", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close
                };
                // Confirmation does not pause or mute the current performance.
                if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            }
            singing.Exit(); ReturnToBrowsing();
        }
        finally { singingExitPrompt = false; singingView.Interact(); }
    }

    private void OnSingingQueueChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => RefreshSinging();
    private void OnSingingPendingChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => RefreshSinging();
    private void RefreshSinging()
    {
        if (singing is null || closed) return;
        var entry = viewModel.Queue.Current;
        var song = entry is null ? null : viewModel.FindSong(entry.SongId);
        capsule.SetSingingInfo(song?.Title ?? entry?.Title, song?.ArtistDetail,
            song?.Cover as Microsoft.UI.Xaml.Media.ImageSource, viewModel.Queue.Pending.Count, singing.Loading, singing.Message);
        singingView.UpdateState(entry is not null, singing.Loading, singing.Ready,
            viewModel.Queue.State == PlaybackState.Paused, viewModel.Queue.Pending.Count, singing.Message, SeekDuringSingingSwitch.IsOn);
        singingView.UpdateTime(singing.Playback.Position, singing.Playback.Duration);
        if (!singing.Loading && !singing.Ready && singing.Message.Length > 0) singingEntryRequested = false;
    }

    private void OnSingingSeekPreferenceChanged(object sender, RoutedEventArgs args)
    {
        if (!ready) return;
        RefreshSinging(); SavePreferences();
    }
}
