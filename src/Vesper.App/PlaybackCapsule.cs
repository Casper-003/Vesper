using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.System;

namespace Vesper.App;

// One centered group. Expanded content is an in-window overlay, never a Window.
internal sealed class PlaybackCapsule : IDisposable
{
    internal readonly ContentControl Bar = new() { Height = 36, Width = 310, Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, IsTabStop = true, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    internal readonly Grid Overlay = new() { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), Visibility = Visibility.Collapsed };
    private readonly Border pill, front, back;
    private readonly TextBlock title = new() { FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
    private readonly ScrollViewer marquee = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Disabled };
    private readonly StackPanel singingPanel = new() { Spacing = 10 };
    private readonly StackPanel previewPanel = new() { Spacing = 8 };
    private readonly TextBlock songTitle = new() { FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock artist = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock rearLabel = new() { FontSize = 10, VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 2) };
    private readonly Image cover = new() { Width = 88, Height = 72, Stretch = Stretch.UniformToFill };
    internal readonly MediaPlayerElement Video = new() { Width = 128, Height = 72, AreTransportControlsEnabled = false };
    internal readonly PreviewTransport Transport = new();
    private readonly PreviewSession session;
    private readonly List<Button> glassButtons = [];
    private readonly nint hwnd;
    private readonly Action routeChanged;
    private readonly CapsuleGesture gesture = new();
    private long wheelAt;
    private bool previewFront;
    private int wheelRemainder;
    private readonly DispatcherTimer marqueeTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly Func<long> clock;
    private readonly Grid root;
    private long marqueeStarted, transitionRevision;
    private bool expanded;
    private Storyboard? morph, widthTransition;
    private readonly CompositeTransform morphTransform = new();
    public bool IsOpen => Overlay.Visibility == Visibility.Visible;
    internal double MarqueeOffset => marquee.HorizontalOffset;
    internal bool IsTransitioning => morph is not null;
    internal Rect CardBounds => front.TransformToVisual(root).TransformBounds(new Rect(0, 0, front.ActualWidth, front.ActualHeight));
    public bool ShowingPreview => IsOpen && previewFront && session.SongId is not null;
    internal event Action? ReturnToDetails;
    internal event Action? BeginSinging;

    internal PlaybackCapsule(Grid root, PreviewSession session, nint hwnd, Action routeChanged, Func<long>? monotonicClock = null)
    {
        this.root = root;
        clock = monotonicClock ?? (() => Environment.TickCount64);
        this.session = session; this.hwnd = hwnd; this.routeChanged = routeChanged;
        marquee.Content = title;
        pill = new Border { CornerRadius = new CornerRadius(18), Padding = new Thickness(14, 0, 14, 0), Child = marquee, BorderThickness = new Thickness(1) };
        Bar.Content = pill;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(Bar, "播放控制台，按 Enter 展开");
        Bar.PointerPressed += Press;
        Bar.PointerMoved += Move;
        Bar.PointerReleased += Release;
        Bar.PointerCanceled += (_, _) => gesture.Cancel();
        Bar.PointerCaptureLost += (_, _) => gesture.Cancel();
        Bar.KeyDown += (_, e) => { if (e.Key is VirtualKey.Enter or VirtualKey.Space) { Open(session.SongId is not null); e.Handled = true; } };
        back = new Border { Width = 396, Height = 140, Margin = new Thickness(0, 70, 0, 0), VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Center, CornerRadius = new CornerRadius(18), IsHitTestVisible = false };
        front = new Border { Width = 420, MinHeight = 150, Margin = new Thickness(0, 54, 0, 0), Padding = new Thickness(14), VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Center, CornerRadius = new CornerRadius(18), BorderThickness = new Thickness(1) };
        Overlay.Children.Add(back); Overlay.Children.Add(front);
        front.RenderTransform = morphTransform;
        front.RenderTransformOrigin = new Point(.5, 0);
        back.Child = rearLabel;
        front.SizeChanged += (_, _) => back.Height = front.ActualHeight;
        Overlay.PointerPressed += (_, e) => { Close(); e.Handled = true; };
        front.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) => e.Handled = true), true);
        front.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, e) => e.Handled = true), true);
        front.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(Wheel), true);
        root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, e) => { if (IsOpen && e.Key == VirtualKey.Escape) { Close(); e.Handled = true; } }), true);
        root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, e) =>
        {
            if (!IsOpen || e.Key != VirtualKey.Tab) return;
            var controls = new List<Control>();
            void Visit(DependencyObject node)
            {
                if (node is UIElement { Visibility: Visibility.Collapsed }) return;
                if (node is Control { IsEnabled: true, IsTabStop: true } control) controls.Add(control);
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Visit(VisualTreeHelper.GetChild(node, i));
            }
            Visit(front);
            if (controls.Count == 0) Bar.Focus(FocusState.Keyboard);
            else
            {
                int index = controls.FindIndex(control => ReferenceEquals(control, FocusManager.GetFocusedElement(root.XamlRoot)));
                int direction = GetKeyState(0x10) < 0 ? -1 : 1;
                controls[(index + direction + controls.Count) % controls.Count].Focus(FocusState.Keyboard);
            }
            e.Handled = true;
        }), true);
        // Focus remains within the open console rather than reaching covered pages.
        previewPanel.TabFocusNavigation = KeyboardNavigationMode.Cycle;
        singingPanel.TabFocusNavigation = KeyboardNavigationMode.Cycle;
        Grid.SetRowSpan(Bar, 2); Grid.SetRowSpan(Overlay, 2);
        root.Children.Add(Bar); root.Children.Add(Overlay);
        var media = new Grid(); media.Children.Add(cover); media.Children.Add(Video);
        var heading = new Grid { ColumnSpacing = 12 };
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); heading.ColumnDefinitions.Add(new ColumnDefinition());
        heading.Children.Add(media);
        var info = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center }; info.Children.Add(songTitle); info.Children.Add(artist);
        Grid.SetColumn(info, 1); heading.Children.Add(info);
        previewPanel.Children.Add(heading); previewPanel.Children.Add(Transport);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        var details = PreviewTransport.Icon("\uE8A7", "返回预览详情"); var close = PreviewTransport.Icon("\uE711", "关闭预览");
        glassButtons.AddRange([details, close]);
        details.Click += (_, _) => { Close(); ReturnToDetails?.Invoke(); };
        close.Click += (_, _) => session.Close(); actions.Children.Add(details); actions.Children.Add(close); previewPanel.Children.Add(actions);
        singingPanel.Children.Add(new TextBlock { Text = "演唱", FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        singingPanel.Children.Add(new TextBlock { Text = "演唱播放尚未接入，当前可从歌曲详情开始预览。", TextWrapping = TextWrapping.Wrap });
        var start = new Button { Content = "开始演唱", IsEnabled = false }; start.Click += (_, _) => BeginSinging?.Invoke(); singingPanel.Children.Add(start);
        glassButtons.Add(start);
        Transport.Bind(session);
        session.Changed += Refresh;
        root.SizeChanged += OnRootSizeChanged;
        marqueeTimer.Tick += (_, _) => TickMarquee();
        marqueeTimer.Start();
        Bar.ActualThemeChanged += (_, _) => Theme(); Theme(); Refresh();
    }
    private void Theme()
    {
        foreach (var border in new[] { pill, front, back }) { border.Background = PreviewTransport.Glass(Bar.ActualTheme); border.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(55, 150, 150, 150)); }
        foreach (var button in glassButtons) PreviewTransport.ApplyGlass(button, Bar.ActualTheme);
    }
    private void Refresh()
    {
        string label = session.SongId is null ? "开始演唱" : session.Singing ? $"返回演唱   ·   🎧 {session.Title}" : $"🎧 {((session.Loading ? "正在准备" : session.Error.Length > 0 ? "预览失败" : session.Ended ? "预览结束" : session.Playback.State == Windows.Media.Playback.MediaPlaybackState.Paused ? "预览已暂停" : "正在预览"))} · {session.Title}";
        if (label != title.Text)
        {
            title.Text = label; marquee.ChangeView(0, null, null, true); marqueeStarted = clock(); ResizeBar();
        }
        songTitle.Text = session.Title; artist.Text = session.Artist; cover.Source = session.Cover;
        Video.Visibility = session.Playback.VideoWidth > 0 ? Visibility.Visible : Visibility.Collapsed;
        cover.Visibility = session.Playback.VideoWidth > 0 ? Visibility.Collapsed : Visibility.Visible;
        if (IsOpen)
        {
            if (session.SongId is null && previewFront) { if (expanded) Close(); return; }
            front.Child = previewFront ? previewPanel : singingPanel;
            back.Visibility = session.SongId is not null ? Visibility.Visible : Visibility.Collapsed;
            rearLabel.Text = previewFront ? "滚轮切换 · 演唱" : "滚轮切换 · 预览";
        }
        routeChanged();
    }
    internal void Open(bool preview)
    {
        bool wasVisible = IsOpen;
        previewFront = preview && session.SongId is not null;
        expanded = true;
        Overlay.Visibility = Visibility.Visible; Refresh(); Morph(true, !wasVisible);
        if (Microsoft.UI.Xaml.Input.FocusManager.FindFirstFocusableElement(front) is Control control) control.Focus(FocusState.Programmatic);
    }
    internal void Close()
    {
        if (!IsOpen || !expanded) return;
        expanded = false; Morph(false);
    }
    private void OnRootSizeChanged(object sender, SizeChangedEventArgs args) => ResizeBar();
    private void ResizeBar()
    {
        title.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double limit = Math.Min(420, Math.Max(140, root.ActualWidth - 280));
        double target = Math.Clamp(Math.Ceiling(title.DesiredSize.Width + 30), 120, limit);
        if (Math.Abs(Bar.Width - target) < .5) return;
        double current = Bar.ActualWidth > 0 ? Bar.ActualWidth : target;
        widthTransition?.Stop(); Bar.Width = current;
        if (!new Windows.UI.ViewManagement.UISettings().AnimationsEnabled) { Bar.Width = target; return; }
        var animation = new DoubleAnimation { From = current, To = target, Duration = TimeSpan.FromMilliseconds(220), EnableDependentAnimation = true, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        var storyboard = new Storyboard(); widthTransition = storyboard;
        Storyboard.SetTarget(animation, Bar); Storyboard.SetTargetProperty(animation, "Width"); storyboard.Children.Add(animation);
        storyboard.Completed += (_, _) => { if (!ReferenceEquals(widthTransition, storyboard)) return; storyboard.Stop(); Bar.Width = target; widthTransition = null; };
        storyboard.Begin();
    }
    // Monotonic elapsed time, independent of Changed events and volume slider reports.
    internal void TickMarquee()
    {
        double distance = marquee.ScrollableWidth;
        if (distance <= 0 || !Bar.IsLoaded) return;
        double travel = distance / 25 * 1000;
        double phase = Math.Max(0, clock() - marqueeStarted) % (3200 + travel);
        double offset = phase <= 1600 ? 0 : Math.Min(distance, (phase - 1600) * .025);
        marquee.ChangeView(offset, null, null, true);
    }
    private void Morph(bool opening, bool fresh = false)
    {
        long request = ++transitionRevision;
        root.UpdateLayout();
        double sx = Math.Clamp(Bar.ActualWidth / Math.Max(1, front.ActualWidth), .1, 1);
        double sy = Math.Clamp(Bar.ActualHeight / Math.Max(1, front.ActualHeight), .05, 1);
        double dy = Bar.TransformToVisual(root).TransformPoint(default).Y - front.TransformToVisual(root).TransformPoint(default).Y + morphTransform.TranslateY;
        // Capture effective values before stopping, so quick reversal continues in place.
        double x = morphTransform.ScaleX, y = morphTransform.ScaleY, offset = morphTransform.TranslateY;
        morph?.Stop();
        morphTransform.ScaleX = fresh ? sx : x; morphTransform.ScaleY = fresh ? sy : y; morphTransform.TranslateY = fresh ? dy : offset;
        Bar.Opacity = 0; Bar.IsHitTestVisible = false;
        void Finish()
        {
            if (request != transitionRevision) return;
            morph?.Stop(); morph = null;
            morphTransform.ScaleX = opening ? 1 : sx; morphTransform.ScaleY = opening ? 1 : sy; morphTransform.TranslateY = opening ? 0 : dy;
            back.Opacity = opening ? 1 : 0;
            if (!opening)
            {
                Overlay.Visibility = Visibility.Collapsed; Bar.Opacity = 1; Bar.IsHitTestVisible = true;
                routeChanged(); Bar.Focus(FocusState.Programmatic);
            }
        }
        if (!new Windows.UI.ViewManagement.UISettings().AnimationsEnabled) { Finish(); return; }
        var storyboard = new Storyboard(); morph = storyboard;
        void Add(DependencyObject target, string property, double from, double to)
        {
            var animation = new DoubleAnimation { From = from, To = to, Duration = TimeSpan.FromMilliseconds(220), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            Storyboard.SetTarget(animation, target); Storyboard.SetTargetProperty(animation, property); storyboard.Children.Add(animation);
        }
        Add(morphTransform, "ScaleX", morphTransform.ScaleX, opening ? 1 : sx);
        Add(morphTransform, "ScaleY", morphTransform.ScaleY, opening ? 1 : sy);
        Add(morphTransform, "TranslateY", morphTransform.TranslateY, opening ? 0 : dy);
        Add(back, "Opacity", opening ? 0 : back.Opacity, opening ? 1 : 0);
        storyboard.Completed += (_, _) => Finish(); storyboard.Begin();
    }
    private void Animate()
    {
        if (!new Windows.UI.ViewManagement.UISettings().AnimationsEnabled) return;
        var animation = new DoubleAnimation { From = .55, To = 1, Duration = TimeSpan.FromMilliseconds(220) };
        var storyboard = new Storyboard(); Storyboard.SetTarget(animation, front); Storyboard.SetTargetProperty(animation, "Opacity"); storyboard.Children.Add(animation); storyboard.Begin();
    }
    private void Wheel(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;
        if (session.SongId is null || Transport.IsDragging) return;
        SwitchCard(e.GetCurrentPoint(front).Properties.MouseWheelDelta);
    }
    internal void SwitchCard(int delta)
    {
        if (!IsOpen || session.SongId is null || Transport.IsDragging) return;
        wheelRemainder += delta;
        if (Math.Abs(wheelRemainder) < 120) return;
        long now = Environment.TickCount64;
        if (now - wheelAt < 220) { wheelRemainder = 0; return; }
        wheelAt = now; wheelRemainder = 0; previewFront = !previewFront; Refresh(); Animate();
    }
    private void Press(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(Bar).Properties.IsLeftButtonPressed) return;
        Point p = e.GetCurrentPoint(Bar).Position;
        gesture.Press(p.X, p.Y, Environment.TickCount64);
        Bar.CapturePointer(e.Pointer); e.Handled = true;
    }
    private void Move(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(Bar).Properties.IsLeftButtonPressed) return;
        Point p = e.GetCurrentPoint(Bar).Position;
        if (!gesture.Move(p.X, p.Y)) return;
        gesture.Cancel(); Bar.ReleasePointerCaptures();
        ReleaseCapture(); GetCursorPos(out var cursor);
        SendMessage(hwnd, 0x00A1, 2, (nint)((cursor.Y << 16) | (cursor.X & 0xffff)));
        e.Handled = true;
    }
    private void Release(object sender, PointerRoutedEventArgs e)
    {
        Point p = e.GetCurrentPoint(Bar).Position;
        bool click = gesture.Release(Environment.TickCount64, p.X >= 0 && p.X < Bar.ActualWidth && p.Y >= 0 && p.Y < Bar.ActualHeight);
        Bar.ReleasePointerCaptures(); e.Handled = true;
        if (click)
            Open(session.SongId is not null && (!session.Singing || p.X < Bar.ActualWidth / 2));
    }
    public void Dispose() { marqueeTimer.Stop(); morph?.Stop(); widthTransition?.Stop(); root.SizeChanged -= OnRootSizeChanged; session.Changed -= Refresh; Transport.Bind(null); }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern nint SendMessage(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern short GetKeyState(int key);
}
