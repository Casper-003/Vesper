using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Vesper.Core;
using Windows.Foundation;

namespace Vesper.App;

public sealed partial class MainWindow
{
    private SingingSession singing = null!;
    private SingingView singingView = null!;
    private readonly DispatcherTimer singingClock = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private bool singingVisible, singingEntryRequested, singingExitPrompt;
    private int singingPresentationRevision, singingPreviewRevision;
    private AppWindowPresenter? browserPresenter;
    private Rect singingEntryBounds;
    private Size singingEntryHostSize;
    private CompositionRoundedRectangleGeometry? singingPreviewClipGeometry;
    private string? singingPreviewFramePath;
    private BitmapImage? singingPreviewFrame;
    private CancellationTokenSource? singingPreviewCancellation;

    private void InitializeSinging(ShellPreferences preferences)
    {
        singingView = new() { Visibility = Visibility.Collapsed };
        Grid.SetRowSpan(singingView, 2); Root.Children.Add(singingView);
        singing = new(DispatcherQueue, viewModel.Queue, () => viewModel.Library.Songs.ToArray(), PresentPreparedSingingAsync);
        singing.Playback.Attach(singingView.Video);
        SeekDuringSingingSwitch.IsOn = preferences.SingingSeekEnabled;
        ConfigureSingingStageButton();
        singing.Changed += RefreshSinging;
        viewModel.Queue.PropertyChanged += OnSingingQueueChanged;
        viewModel.Queue.Pending.CollectionChanged += OnSingingPendingChanged;
        viewModel.PropertyChanged += OnSingingLibraryChanged;
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
            singingPresentationRevision++; singingPreviewRevision++;
            singingPreviewCancellation?.Cancel(); singingPreviewCancellation = null;
            singingClock.Stop(); singing.Dispose(); singingView.Dispose();
            viewModel.Queue.PropertyChanged -= OnSingingQueueChanged;
            viewModel.Queue.Pending.CollectionChanged -= OnSingingPendingChanged;
            viewModel.PropertyChanged -= OnSingingLibraryChanged;
        };
        RefreshSinging();
    }

    private void ConfigureSingingStageButton()
    {
        foreach (Button button in new[] { SingingStagePlayButton, SingingCollapseButton })
        {
            button.Background = SingingView.CreateGlassMaterial();
            foreach (string state in new[] { "ButtonBackground", "ButtonBackgroundPointerOver", "ButtonBackgroundPressed", "ButtonBackgroundDisabled" })
                button.Resources[state] = SingingView.CreateGlassMaterial();
            foreach (string state in new[] { "ButtonBorderBrush", "ButtonBorderBrushPointerOver", "ButtonBorderBrushPressed", "ButtonBorderBrushDisabled" })
                button.Resources[state] = new SolidColorBrush(Colors.Transparent);
        }
        SingingStagePlayButton.PointerEntered += (_, _) => AnimateSingingStageButton(1.06f);
        SingingStagePlayButton.PointerExited += (_, _) => AnimateSingingStageButton(1f);
        SingingStagePlayButton.PointerPressed += (_, _) => AnimateSingingStageButton(.94f);
        SingingStagePlayButton.PointerReleased += (_, _) => AnimateSingingStageButton(1.06f);
        SingingStagePlayButton.PointerCanceled += (_, _) => AnimateSingingStageButton(1f);
        SingingStagePlayButton.PointerCaptureLost += (_, _) => AnimateSingingStageButton(1f);
    }

    private void AnimateSingingStageButton(float scale)
    {
        if (closed) return;
        SingingStagePlayButton.CenterPoint = new Vector3(28, 28, 0);
        var compositor = ElementCompositionPreview.GetElementVisual(Root).Compositor;
        var animation = compositor.CreateVector3KeyFrameAnimation();
        animation.Target = "Scale";
        animation.InsertExpressionKeyFrame(0, "this.StartingValue");
        animation.InsertKeyFrame(1, new Vector3(scale));
        animation.Duration = TimeSpan.FromMilliseconds(120);
        SingingStagePlayButton.StartAnimation(animation);
    }

    private void OnSingingPreviewStageSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (closed) return;
        var visual = ElementCompositionPreview.GetElementVisual(SingingPreviewClip);
        if (singingPreviewClipGeometry is null)
        {
            singingPreviewClipGeometry = visual.Compositor.CreateRoundedRectangleGeometry();
            singingPreviewClipGeometry.CornerRadius = new Vector2(6);
            visual.Clip = visual.Compositor.CreateGeometricClip(singingPreviewClipGeometry);
        }
        singingPreviewClipGeometry.Size = new Vector2((float)Math.Max(0, args.NewSize.Width), (float)Math.Max(0, args.NewSize.Height));
    }

    private void OnSingingStagePlayClick(object sender, RoutedEventArgs args) => OnEnterSinging();

    private Rect GetSingingEntryBounds(Size? targetHostSize = null, Size? referenceHostSize = null)
    {
        Size currentHost = referenceHostSize ?? new Size(Math.Max(1, Root.ActualWidth), Math.Max(1, Root.ActualHeight));
        Size host = targetHostSize ?? new Size(Math.Max(1, Root.ActualWidth), Math.Max(1, Root.ActualHeight));
        FrameworkElement anchor = singingPanelOpen && SingingPreviewStage.ActualWidth > 8 && SingingPreviewStage.ActualHeight > 8
            ? SingingPreviewStage
            : SingingShell.ActualWidth > 8 && SingingShell.ActualHeight > 8 ? SingingShell : QueueSidebarSlot;
        if (anchor.ActualWidth > 8 && anchor.ActualHeight > 8)
        {
            Rect bounds = anchor.TransformToVisual(Root).TransformBounds(new Rect(0, 0, anchor.ActualWidth, anchor.ActualHeight));
            double rightMargin = Math.Max(0, currentHost.Width - bounds.Right);
            double x = Math.Max(0, host.Width - rightMargin - bounds.Width);
            return new Rect(x, bounds.Y, bounds.Width, bounds.Height);
        }
        return new Rect(Math.Max(0, host.Width - 40), 48, 36, Math.Max(120, host.Height * rightSplitRatio));
    }

    private async void OnEnterSinging()
    {
        if (closed || singingExitPrompt || singingEntryRequested) return;
        singingEntryHostSize = new Size(Math.Max(1, Root.ActualWidth), Math.Max(1, Root.ActualHeight));
        singingEntryBounds = GetSingingEntryBounds(singingEntryHostSize, singingEntryHostSize);
        singingEntryRequested = true;
        await monitoring.ReleaseForPlaybackAsync();
        if (closed) return;
        if (singing.Ready && !singing.Loading)
        {
            ShowSinging(); singingEntryRequested = false;
            singingView.UpdateTime(singing.Playback.Position, singing.Playback.Duration);
            await ExpandActiveSingingViewAsync();
        }
        else if (!singing.Loading)
        {
            if (viewModel.Queue.Pending.Count == 0)
            {
                ShowSinging(); singingEntryRequested = false;
                await ExpandActiveSingingViewAsync();
            }
            else await singing.NextAsync();
        }
    }

    private async Task ExpandActiveSingingViewAsync()
    {
        if (closed || !singingVisible) return;
        int request = singingPresentationRevision;
        Size hostSize = new(Math.Max(1, Root.ActualWidth), Math.Max(1, Root.ActualHeight));
        Rect origin = GetSingingEntryBounds(hostSize, singingEntryHostSize);
        singingView.PrepareExpansion(origin, hostSize);
        try
        {
            await singingView.ExpandAsync(origin, hostSize);
        }
        finally
        {
            if (request == singingPresentationRevision) singingView.ResetTransition();
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
        Size hostSize = new(Math.Max(1, Root.ActualWidth), Math.Max(1, Root.ActualHeight));
        Rect origin = GetSingingEntryBounds(hostSize, singingEntryHostSize);
        if (expanding) singingView.PrepareExpansion(origin, hostSize);
        try
        {
            await singingView.PresentFrameAsync(singing.FirstFrame);
            if (closed || !singingVisible || request != singingPresentationRevision) return;
            hostSize = new(Math.Max(1, Root.ActualWidth), Math.Max(1, Root.ActualHeight));
            origin = GetSingingEntryBounds(hostSize, singingEntryHostSize);
            if (expanding) await singingView.ExpandAsync(origin, hostSize);
        }
        finally { if (request == singingPresentationRevision) singingView.ResetTransition(); }
    }

    private void ShowSinging()
    {
        if (singingVisible || closed) return;
        singingVisible = true; singingPresentationRevision++;
        browserPresenter = AppWindow.Presenter;
        AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        Navigation.IsEnabled = false; BackButton.IsEnabled = ForwardButton.IsEnabled = false;
        singingView.Visibility = Visibility.Visible;
        Root.UpdateLayout(); singingView.SetActive(true); RefreshSinging();
    }

    private async void ReturnToBrowsing()
    {
        singingEntryRequested = false;
        int request = ++singingPresentationRevision;
        if (!singingVisible || closed) return;
        singingVisible = false;
        singingView.SetActive(false);
        if (browserPresenter is not null) AppWindow.SetPresenter(browserPresenter);
        browserPresenter = null;
        Navigation.IsEnabled = true; BackButton.IsEnabled = ContentFrame.CanGoBack; ForwardButton.IsEnabled = ContentFrame.CanGoForward;
        Root.UpdateLayout();
        Size hostSize = new(Math.Max(1, Root.ActualWidth), Math.Max(1, Root.ActualHeight));
        Rect target = GetSingingEntryBounds(hostSize, hostSize);
        try
        {
            await singingView.CollapseAsync(target, hostSize);
        }
        finally
        {
            if (request == singingPresentationRevision && !singingVisible && !closed)
            {
                singingView.ResetTransition();
                singingView.Visibility = Visibility.Collapsed;
                if (singingPanelOpen) SingingStagePlayButton.Focus(FocusState.Programmatic);
                else SingingOpenButton.Focus(FocusState.Programmatic);
                DispatcherQueue.TryEnqueue(UpdateTitleLayout);
            }
        }
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
    private void OnSingingLibraryChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.LibraryHero)) RefreshSinging();
    }

    private void RefreshSinging()
    {
        if (singing is null || closed) return;
        var entry = viewModel.Queue.Current;
        var nextEntry = entry ?? viewModel.Queue.Pending.FirstOrDefault();
        var previewSong = nextEntry is null ? null : viewModel.FindSong(nextEntry.SongId);
        SingingStagePlayGlyph.Visibility = singing.Loading ? Visibility.Collapsed : Visibility.Visible;
        SingingStageLoadingRing.IsActive = singing.Loading;
        SingingStageLoadingRing.Visibility = singing.Loading ? Visibility.Visible : Visibility.Collapsed;
        SingingStagePlayButton.IsEnabled = !singing.Loading;
        string playLabel = entry is not null ? "返回演唱" : "开始演唱";
        ToolTipService.SetToolTip(SingingStagePlayButton, playLabel);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SingingStagePlayButton, playLabel);

        string caption = singing.Message.Length > 0 ? singing.Message : previewSong?.Title ?? nextEntry?.Title ?? "";
        SingingPreviewCaption.Text = caption;
        SingingPreviewCaptionBar.Visibility = caption.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateSingingCardArtwork(entry, previewSong);

        singingView.UpdateState(entry is not null, singing.Loading, singing.Ready,
            viewModel.Queue.State == PlaybackState.Paused, viewModel.Queue.Pending.Count, singing.Message, SeekDuringSingingSwitch.IsOn);
        singingView.UpdateTime(singing.Playback.Position, singing.Playback.Duration);
        if (!singing.Loading && !singing.Ready && singing.Message.Length > 0) singingEntryRequested = false;
    }

    private void UpdateSingingCardArtwork(QueueEntry? currentEntry, LibrarySongItem? previewSong)
    {
        if (currentEntry is not null && singing.FirstFrame is not null)
        {
            SetSingingPreviewSource(singing.FirstFrame);
            return;
        }
        string? videoPath = previewSong is null ? null : SingingSession.FindVideoPath(previewSong.Song);
        if (videoPath is not null && string.Equals(singingPreviewFramePath, videoPath, StringComparison.Ordinal) && singingPreviewFrame is not null)
        {
            SetSingingPreviewSource(singingPreviewFrame);
            return;
        }
        SetSingingPreviewSource(previewSong?.Cover as ImageSource);
        if (videoPath is null)
        {
            singingPreviewCancellation?.Cancel();
            singingPreviewCancellation = null;
            return;
        }
        _ = LoadSingingPreviewFrameAsync(videoPath);
    }

    private void SetSingingPreviewSource(ImageSource? source)
    {
        SingingPreviewImage.Source = source;
        SingingPreviewImage.Visibility = source is null ? Visibility.Collapsed : Visibility.Visible;
        SingingPreviewPlaceholder.Visibility = source is null ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task LoadSingingPreviewFrameAsync(string videoPath)
    {
        int request = ++singingPreviewRevision;
        singingPreviewCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        singingPreviewCancellation = cancellation;
        try
        {
            var frame = await SingingSession.ReadFirstFrameAsync(videoPath, cancellation.Token);
            if (closed || request != singingPreviewRevision) return;
            singingPreviewFramePath = videoPath;
            singingPreviewFrame = frame;
            SetSingingPreviewSource(frame);
        }
        catch
        {
            // Keep cover or stage placeholder if video thumbnail decoding fails.
        }
        finally
        {
            if (ReferenceEquals(singingPreviewCancellation, cancellation)) singingPreviewCancellation = null;
        }
    }

    private void OnSingingSeekPreferenceChanged(object sender, RoutedEventArgs args)
    {
        if (!ready) return;
        RefreshSinging(); SavePreferences();
    }
}
