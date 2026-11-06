using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.System;

namespace Vesper.App;

internal sealed class SingingView : UserControl, IDisposable
{
    private sealed class SeekSlider : Slider
    {
        protected override void OnPointerWheelChanged(PointerRoutedEventArgs e) => e.Handled = true;
    }
    internal readonly MediaPlayerElement Video = new() { AreTransportControlsEnabled = false, Stretch = Stretch.Uniform };
    private readonly Image firstFrame = new() { Stretch = Stretch.Uniform, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly Grid layout = new() { Background = new SolidColorBrush(Colors.Black) };
    private readonly Grid toolbar = new() { RowSpacing = 12, Margin = new Thickness(32, 0, 32, 28), VerticalAlignment = VerticalAlignment.Bottom };
    internal readonly Slider Progress = new SeekSlider { Minimum = 0, Maximum = 1, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ProgressBar readOnlyProgress = new() { Minimum = 0, Maximum = 1, Height = 4, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock elapsed = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock duration = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
    internal readonly Button Back = GlassButton("\uE72B", "返回选歌");
    internal readonly Button Pause = GlassButton("\uE769", "暂停演唱");
    internal readonly Button Next = GlassButton("\uE893", "切歌");
    internal readonly Button ExitButton = GlassButton("\uE8BB", "退出本场");
    internal readonly Button Start = new() { Content = "开始演唱", HorizontalAlignment = HorizontalAlignment.Center, MinWidth = 120 };
    private readonly TextBlock waitingTitle = new() { FontSize = 28, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextAlignment = TextAlignment.Center };
    private readonly TextBlock waitingText = new() { FontSize = 16, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 560 };
    private readonly StackPanel waiting = new() { Spacing = 18, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(32, 24, 32, 156) };
    private readonly ProgressRing loading = new() { Width = 32, Height = 32, IsActive = false, Visibility = Visibility.Collapsed };
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly CompositeTransform expansion = new();
    private Storyboard? transition;
    private bool syncing, dragging, keyboardInControls, active, pinned, wasPaused, allowSeek = true;
    private long lastInteraction;
    private string pauseLabel = "";
    internal bool ControlsVisible => toolbar.Visibility == Visibility.Visible;
    internal bool IsDragging => dragging;
    public event Action? ReturnRequested, PauseRequested, NextRequested, ExitRequested, StartRequested;
    public event Action<TimeSpan>? SeekRequested;

    public SingingView()
    {
        RequestedTheme = ElementTheme.Dark; IsTabStop = true; Content = layout;
        layout.Children.Add(Video); layout.Children.Add(firstFrame);
        waiting.Children.Add(loading); waiting.Children.Add(waitingTitle); waiting.Children.Add(waitingText); waiting.Children.Add(Start);
        layout.Children.Add(waiting);
        toolbar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        toolbar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var timeline = new Grid { ColumnSpacing = 16 };
        timeline.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        timeline.ColumnDefinitions.Add(new ColumnDefinition());
        timeline.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        var track = new Grid(); track.Children.Add(readOnlyProgress); track.Children.Add(Progress);
        timeline.Children.Add(elapsed); Grid.SetColumn(track, 1); timeline.Children.Add(track);
        Grid.SetColumn(duration, 2); timeline.Children.Add(duration); toolbar.Children.Add(timeline);
        var buttons = new Grid();
        for (int i = 0; i < 3; i++) buttons.ColumnDefinitions.Add(new ColumnDefinition());
        Back.HorizontalAlignment = HorizontalAlignment.Left; buttons.Children.Add(Back);
        var central = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 28, HorizontalAlignment = HorizontalAlignment.Center };
        central.Children.Add(Pause); central.Children.Add(Next); Grid.SetColumn(central, 1); buttons.Children.Add(central);
        ExitButton.HorizontalAlignment = HorizontalAlignment.Right; Grid.SetColumn(ExitButton, 2); buttons.Children.Add(ExitButton);
        Grid.SetRow(buttons, 1); toolbar.Children.Add(buttons); layout.Children.Add(toolbar);
        RenderTransform = expansion; RenderTransformOrigin = new Point(.5, .5);
        TabFocusNavigation = KeyboardNavigationMode.Cycle;
        Back.Click += (_, _) => ReturnRequested?.Invoke();
        Pause.Click += (_, _) => PauseRequested?.Invoke();
        Next.Click += (_, _) => NextRequested?.Invoke();
        ExitButton.Click += (_, _) => ExitRequested?.Invoke();
        Start.Click += (_, _) => StartRequested?.Invoke();
        Progress.ValueChanged += (_, _) => { if (!syncing && allowSeek) { Interact(); SeekRequested?.Invoke(TimeSpan.FromSeconds(Progress.Value)); } };
        Progress.AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => { dragging = true; Interact(); }), true);
        void EndDrag() { dragging = false; Interact(); }
        Progress.AddHandler(PointerReleasedEvent, new PointerEventHandler((_, _) => EndDrag()), true);
        Progress.AddHandler(PointerCanceledEvent, new PointerEventHandler((_, _) => EndDrag()), true);
        Progress.AddHandler(PointerCaptureLostEvent, new PointerEventHandler((_, _) => EndDrag()), true);
        AddHandler(PointerMovedEvent, new PointerEventHandler((_, _) => Interact()), true);
        AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => Interact()), true);
        toolbar.GotFocus += (_, e) => keyboardInControls = e.OriginalSource is Control { FocusState: FocusState.Keyboard };
        // Hiding the bar can itself move focus. That is not a new user action and
        // must not immediately reopen the controls after continuing the song.
        toolbar.LostFocus += (_, _) => keyboardInControls = false;
        KeyDown += (_, e) =>
        {
            Interact();
            if (e.Key == VirtualKey.Escape) { ReturnRequested?.Invoke(); e.Handled = true; }
        };
        timer.Tick += (_, _) => RefreshVisibility();
        Loaded += (_, _) => timer.Start();
        Unloaded += (_, _) => timer.Stop();
        AutomationProperties.SetName(Progress, "演唱进度");
        AutomationProperties.SetName(readOnlyProgress, "演唱进度，仅显示");
        AutomationProperties.SetName(this, "演唱界面");
        foreach (var button in new[] { Back, Pause, Next, ExitButton, Start })
        {
            button.Background = Material(); button.BorderThickness = new Thickness(0);
            foreach (string state in new[] { "ButtonBackground", "ButtonBackgroundPointerOver", "ButtonBackgroundPressed", "ButtonBackgroundDisabled" })
                button.Resources[state] = Material();
            foreach (string state in new[] { "ButtonBorderBrush", "ButtonBorderBrushPointerOver", "ButtonBorderBrushPressed", "ButtonBorderBrushDisabled" })
                button.Resources[state] = new SolidColorBrush(Colors.Transparent);
        }
    }

    private static AcrylicBrush Material() => new()
    {
        TintColor = Windows.UI.Color.FromArgb(255, 28, 28, 32), TintOpacity = .35,
        TintLuminosityOpacity = .5, AlwaysUseFallback = false,
        FallbackColor = Windows.UI.Color.FromArgb(255, 48, 48, 52)
    };
    private static Button GlassButton(string glyph, string name)
    {
        var button = new Button { Width = 52, Height = 52, Padding = new Thickness(0), CornerRadius = new CornerRadius(26),
            Content = new FontIcon { Glyph = glyph, FontSize = 21 } };
        AutomationProperties.SetName(button, name); ToolTipService.SetToolTip(button, name);
        return button;
    }
    public void Interact() { lastInteraction = Environment.TickCount64; toolbar.Visibility = Visibility.Visible; }
    private void RefreshVisibility()
    {
        toolbar.Visibility = !active || pinned || dragging || keyboardInControls || Environment.TickCount64 - lastInteraction < 3000
            ? Visibility.Visible : Visibility.Collapsed;
    }
    public void SetActive(bool value)
    {
        active = value; dragging = keyboardInControls = false;
        if (value) { Interact(); Back.Focus(FocusState.Programmatic); }
    }
    internal void UpdateState(bool hasSong, bool preparing, bool ready, bool paused, int pending, string message, bool canSeek)
    {
        bool resumed = wasPaused && !paused && ready && !preparing;
        wasPaused = paused;
        pinned = paused || preparing || !hasSong;
        allowSeek = canSeek;
        Progress.Visibility = canSeek ? Visibility.Visible : Visibility.Collapsed;
        readOnlyProgress.Visibility = canSeek ? Visibility.Collapsed : Visibility.Visible;
        Progress.IsEnabled = canSeek && ready && !preparing;
        Pause.IsEnabled = ready && !preparing; Next.IsEnabled = hasSong && !preparing;
        string label = paused ? "继续演唱" : "暂停演唱";
        if (pauseLabel != label)
        {
            pauseLabel = label; Pause.Content = new FontIcon { Glyph = paused ? "\uE768" : "\uE769", FontSize = 21 };
            AutomationProperties.SetName(Pause, label); ToolTipService.SetToolTip(Pause, label);
        }
        waiting.Visibility = !hasSong || preparing && !ready ? Visibility.Visible : Visibility.Collapsed;
        waitingTitle.Text = preparing ? "正在准备演唱" : message.Length > 0 ? "暂时无法演唱" : "等待演唱";
        waitingText.Text = message.Length > 0 ? message : pending > 0 ? $"待播 {pending} 首歌曲" : "暂无待播歌曲，返回选歌后添加歌曲。";
        loading.IsActive = preparing; loading.Visibility = preparing ? Visibility.Visible : Visibility.Collapsed;
        Start.Visibility = !hasSong && !preparing && pending > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (!hasSong) { firstFrame.Source = null; firstFrame.Visibility = Visibility.Collapsed; }
        if (resumed && active)
        {
            Focus(FocusState.Programmatic); keyboardInControls = false;
            lastInteraction = Environment.TickCount64 - 3001;
        }
        RefreshVisibility();
    }
    internal void UpdateTime(TimeSpan position, TimeSpan length)
    {
        syncing = true;
        try
        {
            Progress.Maximum = readOnlyProgress.Maximum = Math.Max(1, length.TotalSeconds);
            if (!dragging) Progress.Value = Math.Clamp(position.TotalSeconds, 0, Progress.Maximum);
            readOnlyProgress.Value = Math.Clamp(position.TotalSeconds, 0, readOnlyProgress.Maximum);
            elapsed.Text = Format(position); duration.Text = Format(length);
            if (position > TimeSpan.FromMilliseconds(100)) firstFrame.Visibility = Visibility.Collapsed;
        }
        finally { syncing = false; }
    }
    private static string Format(TimeSpan value) => value.TotalHours >= 1 ? value.ToString(@"h\:mm\:ss") : value.ToString(@"m\:ss");

    internal async Task PresentFrameAsync(Microsoft.UI.Xaml.Media.Imaging.BitmapImage? image)
    {
        firstFrame.Source = image; firstFrame.Visibility = image is null ? Visibility.Collapsed : Visibility.Visible;
        // The decoded image is the visible expansion surface. Two render ticks give
        // XAML a chance to commit it; native video presentation is verified manually.
        var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int frames = 0;
        void Rendering(object? sender, object e) { if (++frames >= 2) rendered.TrySetResult(); }
        CompositionTarget.Rendering += Rendering;
        try { await rendered.Task.WaitAsync(TimeSpan.FromSeconds(3)); }
        finally { CompositionTarget.Rendering -= Rendering; }
    }

    internal void PrepareExpansion(Rect origin, Size hostSize)
    {
        transition?.Stop();
        if (!new Windows.UI.ViewManagement.UISettings().AnimationsEnabled) { ResetTransition(); return; }
        expansion.ScaleX = Math.Clamp(origin.Width / Math.Max(1, hostSize.Width), .05, 1);
        expansion.ScaleY = Math.Clamp(origin.Height / Math.Max(1, hostSize.Height), .05, 1);
        expansion.TranslateX = origin.X + origin.Width / 2 - hostSize.Width / 2;
        expansion.TranslateY = origin.Y + origin.Height / 2 - hostSize.Height / 2;
    }
    internal async Task ExpandAsync(Rect origin, Size hostSize)
    {
        PrepareExpansion(origin, hostSize);
        if (!new Windows.UI.ViewManagement.UISettings().AnimationsEnabled) return;
        var storyboard = new Storyboard(); transition = storyboard;
        foreach (var pair in new[] { ("ScaleX", 1d), ("ScaleY", 1d), ("TranslateX", 0d), ("TranslateY", 0d) })
        {
            var animation = new DoubleAnimation { To = pair.Item2, Duration = TimeSpan.FromMilliseconds(260),
                EnableDependentAnimation = true, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            Storyboard.SetTarget(animation, expansion); Storyboard.SetTargetProperty(animation, pair.Item1); storyboard.Children.Add(animation);
        }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        storyboard.Completed += (_, _) => completion.TrySetResult(); storyboard.Begin();
        try { await completion.Task.WaitAsync(TimeSpan.FromSeconds(2)); }
        finally { ResetTransition(); }
    }
    internal void ResetTransition()
    {
        transition?.Stop(); transition = null;
        expansion.ScaleX = expansion.ScaleY = 1; expansion.TranslateX = expansion.TranslateY = 0;
    }
    public void Dispose() { timer.Stop(); ResetTransition(); }
}
