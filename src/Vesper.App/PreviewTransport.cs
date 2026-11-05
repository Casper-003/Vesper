using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Markup;
using Windows.Media.Playback;

namespace Vesper.App;

internal sealed class PreviewTransport : UserControl
{
    private sealed class TransportSlider : Slider
    {
        protected override void OnPointerWheelChanged(PointerRoutedEventArgs e) => e.Handled = true;
    }
    private readonly Slider progress = new TransportSlider { Minimum = 0, Maximum = 1, MinWidth = 0 };
    private readonly Slider volume = new TransportSlider { Minimum = 0, Maximum = 100, Width = 80 };
    private readonly TextBlock clock = new() { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button pause = Icon("\uE769", "暂停／继续预览");
    private readonly Button mute = Icon("\uE767", "静音／恢复预览声音");
    private readonly Border chrome;
    private readonly bool capsuleMode;
    internal readonly Button ReturnButton = FilledButton("return", "返回到详情页");
    internal readonly Button StopButton = FilledButton("stop", "停止预览");
    internal Button PauseButton => pause;
    internal Slider Progress => progress;
    internal event Action? ReturnRequested;
    internal event Action? StopRequested;
    private bool updating, dragging;
    private PreviewSession? session;
    public bool IsDragging => dragging;
    internal static Brush Glass(ElementTheme theme) => new AcrylicBrush
    {
        TintColor = theme == ElementTheme.Light ? Colors.White : Colors.Black,
        TintOpacity = .58,
        FallbackColor = theme == ElementTheme.Light ? Windows.UI.Color.FromArgb(255, 235, 235, 238) : Windows.UI.Color.FromArgb(255, 40, 40, 44)
    };
    internal static Button Icon(string glyph, string label)
    {
        var button = new Button { Content = new FontIcon { Glyph = glyph, FontSize = 16 }, Width = 34, Height = 32, Padding = new Thickness(0), CornerRadius = new CornerRadius(16) };
        AutomationProperties.SetName(button, label); ToolTipService.SetToolTip(button, label);
        return button;
    }
    internal static void ApplyGlass(Button button, ElementTheme theme)
    {
        button.Background = Glass(theme);
        foreach (string state in new[] { "ButtonBackground", "ButtonBackgroundPointerOver", "ButtonBackgroundPressed", "ButtonBackgroundDisabled" })
            button.Resources[state] = Glass(theme);
    }
    private static PathIcon FilledIcon(string kind)
    {
        string data = kind switch
        {
            "return" => "M18,2 L24,7 L18,12 L18,9 L8,9 C4,9 3,11 3,14 C3,17 5,19 8,19 L15,19 L15,22 L8,22 C3,22 0,19 0,14 C0,9 3,6 8,6 L18,6 Z",
            "pause" => "M3,2 L9,2 L9,22 L3,22 Z M15,2 L21,2 L21,22 L15,22 Z",
            "stop" => "M3,3 L21,3 L21,21 L3,21 Z",
            _ => "M5,2 L22,12 L5,22 Z"
        };
        return (PathIcon)XamlReader.Load($"<PathIcon xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Width='18' Height='18' Data='{data}'/>");
    }
    private static Button FilledButton(string kind, string label)
    {
        var button = new Button { Content = FilledIcon(kind), Width = 42, Height = 36, Padding = new Thickness(8), BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(0) };
        AutomationProperties.SetName(button, label); ToolTipService.SetToolTip(button, label);
        foreach (string state in new[] { "ButtonBackground", "ButtonBackgroundPointerOver", "ButtonBackgroundPressed", "ButtonBackgroundDisabled" })
            button.Resources[state] = new SolidColorBrush(Colors.Transparent);
        button.Background = new SolidColorBrush(Colors.Transparent);
        return button;
    }
    public PreviewTransport(bool capsuleMode = false)
    {
        this.capsuleMode = capsuleMode;
        var layout = new Grid { ColumnSpacing = 8, RowSpacing = 0 };
        foreach (var width in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto }) layout.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        if (capsuleMode)
        {
            pause = FilledButton("pause", "暂停／继续预览");
            layout.ColumnDefinitions.Clear();
            for (int i = 0; i < 3; i++) layout.ColumnDefinitions.Add(new ColumnDefinition());
            layout.Children.Add(ReturnButton); Grid.SetColumn(pause, 1); layout.Children.Add(pause);
            Grid.SetColumn(StopButton, 2); layout.Children.Add(StopButton);
            foreach (var button in new[] { ReturnButton, pause, StopButton }) button.HorizontalAlignment = HorizontalAlignment.Center;
            Grid.SetRow(progress, 1); Grid.SetColumnSpan(progress, 3); layout.Children.Add(progress);
            ReturnButton.Click += (_, _) => ReturnRequested?.Invoke();
            StopButton.Click += (_, _) => StopRequested?.Invoke();
        }
        else
        {
            Grid.SetColumnSpan(progress, 4); layout.Children.Add(progress);
            Grid.SetRow(pause, 1); layout.Children.Add(pause);
            Grid.SetRow(clock, 1); Grid.SetColumn(clock, 1); layout.Children.Add(clock);
            Grid.SetRow(mute, 1); Grid.SetColumn(mute, 2); layout.Children.Add(mute);
            Grid.SetRow(volume, 1); Grid.SetColumn(volume, 3); layout.Children.Add(volume);
        }
        chrome = new Border { Padding = capsuleMode ? new Thickness(0) : new Thickness(8, 4, 8, 6), CornerRadius = new CornerRadius(12), Child = layout, BorderThickness = capsuleMode ? new Thickness(0) : new Thickness(1) };
        Content = chrome;
        SizeChanged += (_, _) => { clock.Visibility = ActualWidth < 280 ? Visibility.Collapsed : Visibility.Visible; volume.Width = ActualWidth < 280 ? 48 : 80; };
        AutomationProperties.SetName(progress, "预览进度"); AutomationProperties.SetName(volume, "预览音量");
        ActualThemeChanged += (_, _) => Theme();
        // A detached control can resolve its initial theme before joining the
        // window; Loaded must apply the final inherited palette as well.
        Loaded += (_, _) => Theme();
        Theme();
        pause.Click += (_, _) => session?.TogglePause();
        mute.Click += (_, _) => { if (session is { } current) current.SetMuted(!current.Playback.IsMuted); };
        volume.ValueChanged += (_, _) => { if (!updating && session is { } current) current.SetVolume(volume.Value / 100); };
        progress.ValueChanged += (_, _) => { if (!updating && session is { Loading: false, Error.Length: 0 } current) current.Playback.Seek(TimeSpan.FromSeconds(progress.Value)); };
        foreach (var slider in new[] { progress, volume })
        {
            slider.AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => dragging = true), true);
            slider.AddHandler(PointerReleasedEvent, new PointerEventHandler((_, _) => dragging = false), true);
            slider.AddHandler(PointerCaptureLostEvent, new PointerEventHandler((_, _) => dragging = false), true);
            slider.AddHandler(PointerCanceledEvent, new PointerEventHandler((_, _) => dragging = false), true);
        }
    }
    private void Theme()
    {
        if (capsuleMode)
        {
            ApplyCapsuleTheme(RequestedTheme == ElementTheme.Default ? ActualTheme : RequestedTheme);
            return;
        }
        chrome.Background = Glass(ActualTheme); ApplyGlass(pause, ActualTheme); ApplyGlass(mute, ActualTheme);
        chrome.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(45, 150, 150, 150));
    }
    internal void ApplyCapsuleTheme(ElementTheme theme)
    {
        if (!capsuleMode) return;
        chrome.Background = new SolidColorBrush(Colors.Transparent);
        foreach (var button in new[] { ReturnButton, pause, StopButton })
        {
            var brush = new SolidColorBrush(theme == ElementTheme.Dark ? Colors.White : Colors.Black);
            button.Foreground = brush;
            if (button.Content is IconElement icon) icon.Foreground = brush;
        }
    }
    internal void Bind(PreviewSession? value)
    {
        if (session is not null) session.Changed -= Refresh;
        session = value;
        if (session is not null) session.Changed += Refresh;
        dragging = false; Refresh();
    }
    private static string Time(TimeSpan value) => $"{(int)value.TotalMinutes}:{value.Seconds:00}";
    private void Refresh()
    {
        updating = true;
        try
        {
            bool usable = session is { SongId: not null, Loading: false, Error.Length: 0 };
            pause.IsEnabled = progress.IsEnabled = usable;
            ReturnButton.IsEnabled = StopButton.IsEnabled = session?.SongId is not null;
            volume.IsEnabled = mute.IsEnabled = usable && !session!.Singing;
            if (session is null) return;
            progress.Maximum = Math.Max(1, session.Playback.Duration.TotalSeconds);
            if (!dragging) progress.Value = session.Playback.Position.TotalSeconds;
            if (!dragging) volume.Value = session.Playback.Volume * 100;
            bool playing = session.Playback.State == MediaPlaybackState.Playing && !session.Ended;
            if (capsuleMode)
            {
                string kind = playing ? "pause" : "play";
                if (!Equals(pause.Tag, kind))
                {
                    var icon = FilledIcon(kind);
                    ElementTheme theme = RequestedTheme == ElementTheme.Default ? ActualTheme : RequestedTheme;
                    icon.Foreground = new SolidColorBrush(theme == ElementTheme.Dark ? Colors.White : Colors.Black);
                    pause.Content = icon; pause.Tag = kind;
                }
            }
            else ((FontIcon)pause.Content).Glyph = playing ? "\uE769" : "\uE768";
            ((FontIcon)mute.Content).Glyph = session.Playback.IsMuted ? "\uE74F" : "\uE767";
            clock.Text = session.Loading ? "正在准备预览…" : session.Error.Length > 0 ? session.Error
                : $"{Time(session.Playback.Position)} / {Time(session.Playback.Duration)}" + (session.Singing ? " · 演唱中，预览静音" : session.Ended ? " · 预览结束" : "");
            clock.TextWrapping = TextWrapping.Wrap;
        }
        finally { updating = false; }
    }
}
