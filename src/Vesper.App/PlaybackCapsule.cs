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
    private int wheelRemainder, marqueeTicks;
    public bool IsOpen => Overlay.Visibility == Visibility.Visible;
    public bool ShowingPreview => IsOpen && previewFront && session.SongId is not null;
    internal event Action? ReturnToDetails;
    internal event Action? BeginSinging;

    internal PlaybackCapsule(Grid root, PreviewSession session, nint hwnd, Action routeChanged)
    {
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
        if (label != title.Text) { title.Text = label; marquee.ChangeView(0, null, null, true); marqueeTicks = 0; }
        else if (marquee.ScrollableWidth > 0 && ++marqueeTicks > 8)
        {
            double offset = marquee.HorizontalOffset + 5;
            if (offset > marquee.ScrollableWidth + 20) { offset = 0; marqueeTicks = 0; }
            marquee.ChangeView(Math.Min(offset, marquee.ScrollableWidth), null, null, true);
            if (marquee.HorizontalOffset >= marquee.ScrollableWidth) { if (marqueeTicks % 12 == 0) { marquee.ChangeView(0, null, null, true); marqueeTicks = 0; } }
        }
        songTitle.Text = session.Title; artist.Text = session.Artist; cover.Source = session.Cover;
        Video.Visibility = session.Playback.VideoWidth > 0 ? Visibility.Visible : Visibility.Collapsed;
        cover.Visibility = session.Playback.VideoWidth > 0 ? Visibility.Collapsed : Visibility.Visible;
        if (IsOpen)
        {
            if (session.SongId is null && previewFront) { Close(); return; }
            front.Child = previewFront ? previewPanel : singingPanel;
            back.Visibility = session.SongId is not null ? Visibility.Visible : Visibility.Collapsed;
            rearLabel.Text = previewFront ? "滚轮切换 · 演唱" : "滚轮切换 · 预览";
        }
        routeChanged();
    }
    internal void Open(bool preview)
    {
        previewFront = preview && session.SongId is not null;
        Overlay.Visibility = Visibility.Visible; Refresh(); Animate();
        if (Microsoft.UI.Xaml.Input.FocusManager.FindFirstFocusableElement(front) is Control control) control.Focus(FocusState.Programmatic);
    }
    internal void Close()
    {
        Overlay.Visibility = Visibility.Collapsed; routeChanged(); Bar.Focus(FocusState.Programmatic);
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
    public void Dispose() { session.Changed -= Refresh; Transport.Bind(null); }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern nint SendMessage(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern short GetKeyState(int key);
}
