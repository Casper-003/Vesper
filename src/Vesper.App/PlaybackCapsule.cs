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
    private readonly Border pill, singingCard, previewCard;
    private Border front => previewFront ? previewCard : singingCard;
    private Border back => previewFront ? singingCard : previewCard;
    private readonly Grid deck = new() { Width = 420, Margin = new Thickness(0, 54, 0, 0), VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock title = new() { FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
    private readonly ScrollViewer marquee = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Disabled };
    private readonly StackPanel singingPanel = new() { Spacing = 10 };
    private readonly StackPanel previewPanel = new() { Spacing = 8 };
    private readonly TextBlock songTitle = new() { FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock artist = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Image cover = new() { Width = 88, Height = 88, Stretch = Stretch.UniformToFill };
    internal readonly MediaPlayerElement Video = new() { Width = 128, Height = 72, AreTransportControlsEnabled = false };
    internal readonly PreviewTransport Transport = new(capsuleMode: true);
    private readonly PreviewSession session;
    private readonly List<Button> glassButtons = [];
    private readonly nint hwnd;
    private readonly Action routeChanged;
    private readonly CapsuleGesture gesture = new();
    private bool previewFront;
    private int wheelRemainder;
    private readonly DispatcherTimer marqueeTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly Func<long> clock;
    private readonly Grid root;
    private readonly long hostThemeToken;
    private long marqueeStarted, transitionRevision;
    private bool expanded;
    private Storyboard? morph, widthTransition;
    private readonly CompositeTransform morphTransform = new();
    private readonly CompositeTransform singingPose = new(), previewPose = new();
    private Storyboard? cardSwitch;
    private long cardRevision;
    public bool IsOpen => Overlay.Visibility == Visibility.Visible;
    internal double MarqueeOffset => marquee.HorizontalOffset;
    internal string Caption => title.Text;
    internal bool IsTransitioning => morph is not null || cardSwitch is not null;
    internal bool IsCardSwitching => cardSwitch is not null;
    internal Brush PanelMaterial => front.Background;
    internal Rect CardBounds => front.TransformToVisual(root).TransformBounds(new Rect(0, 0, front.ActualWidth, front.ActualHeight));
    internal Rect SingingBounds => singingCard.TransformToVisual(root).TransformBounds(new Rect(0, 0, singingCard.ActualWidth, singingCard.ActualHeight));
    internal Rect PreviewBounds => previewCard.TransformToVisual(root).TransformBounds(new Rect(0, 0, previewCard.ActualWidth, previewCard.ActualHeight));
    internal FrameworkElement Deck => deck;
    internal bool PersistentCardContents => ReferenceEquals(singingCard.Child, singingPanel) && ReferenceEquals(previewCard.Child, previewPanel);
    internal bool RearIsInteractive => back.IsHitTestVisible;
    internal (int Singing, int Preview) DeckOrder => (Canvas.GetZIndex(singingCard), Canvas.GetZIndex(previewCard));
    public bool ShowingPreview => IsOpen && previewFront && session.SongId is not null;
    internal bool NeedsPreviewSurface => IsOpen && session.SongId is not null;
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
        Bar.KeyDown += (_, e) => { if (e.Key is VirtualKey.Enter or VirtualKey.Space) { if (!IsCardSwitching) Open(session.SongId is not null); e.Handled = true; } };
        static Border Card(CompositeTransform pose) => new()
        {
            Width = 420, MinHeight = 150, Padding = new Thickness(14), VerticalAlignment = VerticalAlignment.Top,
            CornerRadius = new CornerRadius(18), BorderThickness = new Thickness(1),
            RenderTransform = pose, RenderTransformOrigin = new Point(.5, 0)
        };
        singingCard = Card(singingPose); previewCard = Card(previewPose);
        deck.Children.Add(singingCard); deck.Children.Add(previewCard); Overlay.Children.Add(deck);
        deck.RenderTransform = morphTransform; deck.RenderTransformOrigin = new Point(.5, 0);
        // The overlay already shields covered siblings. Do not mark child press/
        // release handled: native Button/Slider need the full gesture themselves.
        Overlay.PointerPressed += (_, e) =>
        {
            if (IsCardSwitching)
            {
                Point p = e.GetCurrentPoint(deck).Position;
                if (p.X >= 0 && p.X <= deck.ActualWidth && p.Y >= -16 && p.Y <= deck.ActualHeight + 48)
                { e.Handled = true; return; }
            }
            for (DependencyObject? node = e.OriginalSource as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
                if (ReferenceEquals(node, front)) return;
            Close(); e.Handled = true;
        };
        deck.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(Wheel), true);
        root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, e) => { if (IsOpen && e.Key == VirtualKey.Escape) { Close(); e.Handled = true; } }), true);
        root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, e) =>
        {
            if (!IsOpen || e.Key != VirtualKey.Tab) return;
            if (IsCardSwitching) { e.Handled = true; return; }
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
        var info = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center }; info.Children.Add(songTitle); info.Children.Add(artist); info.Children.Add(Transport);
        Grid.SetColumn(info, 1); heading.Children.Add(info);
        previewPanel.Children.Add(heading);
        Transport.ReturnRequested += () => { Close(); ReturnToDetails?.Invoke(); };
        Transport.StopRequested += session.Close;
        singingPanel.Children.Add(new TextBlock { Text = "演唱", FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        singingPanel.Children.Add(new TextBlock { Text = "演唱播放尚未接入，当前可从歌曲详情开始预览。", TextWrapping = TextWrapping.Wrap });
        var start = new Button { Content = "开始演唱", IsEnabled = false }; start.Click += (_, _) => BeginSinging?.Invoke(); singingPanel.Children.Add(start);
        glassButtons.Add(start);
        singingCard.Child = singingPanel; previewCard.Child = previewPanel;
        SettleDeck();
        Transport.Bind(session);
        session.Changed += Refresh;
        root.SizeChanged += OnRootSizeChanged;
        hostThemeToken = root.RegisterPropertyChangedCallback(FrameworkElement.RequestedThemeProperty, (_, _) => Theme());
        root.ActualThemeChanged += OnHostThemeChanged;
        marqueeTimer.Tick += (_, _) => TickMarquee();
        marqueeTimer.Start();
        Bar.ActualThemeChanged += (_, _) => Theme(); Theme(); Refresh();
    }
    private void Theme()
    {
        ElementTheme theme = root.RequestedTheme == ElementTheme.Default ? root.ActualTheme : root.RequestedTheme;
        Transport.RequestedTheme = theme;
        Transport.ApplyCapsuleTheme(theme);
        pill.Background = PreviewTransport.Glass(theme);
        foreach (var border in new[] { singingCard, previewCard }) border.Background = PreviewTransport.FrostedPanel(theme);
        foreach (var border in new[] { pill, singingCard, previewCard }) border.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(55, 150, 150, 150));
        foreach (var button in glassButtons) PreviewTransport.ApplyGlass(button, theme);
    }
    private void OnHostThemeChanged(FrameworkElement sender, object args) => Theme();
    private void Refresh()
    {
        string label = session.SongId is null ? "🪩" : session.Singing ? $"🪩 返回演唱   ·   🎵 {session.Title}" : $"🎵 {((session.Loading ? "正在准备" : session.Error.Length > 0 ? "预览失败" : session.Ended ? "预览结束" : session.Playback.State == Windows.Media.Playback.MediaPlaybackState.Paused ? "预览已暂停" : "正在听取"))} · {session.Title}";
        if (label != title.Text)
        {
            title.FontSize = session.SongId is null ? 20 : 13;
            ToolTipService.SetToolTip(Bar, session.SongId is null ? "开始演唱" : "打开播放控制");
            title.Text = label; marquee.ChangeView(0, null, null, true); marqueeStarted = clock(); ResizeBar();
        }
        songTitle.Text = session.Title; artist.Text = session.Artist; cover.Source = session.Cover;
        Video.Visibility = session.Playback.VideoWidth > 0 ? Visibility.Visible : Visibility.Collapsed;
        cover.Visibility = session.Playback.VideoWidth > 0 ? Visibility.Collapsed : Visibility.Visible;
        if (IsOpen)
        {
            if (session.SongId is null && (previewFront || IsCardSwitching)) { if (expanded) Close(); return; }
            previewCard.Visibility = session.SongId is not null ? Visibility.Visible : Visibility.Collapsed;
            if (!IsCardSwitching && expanded && morph is null) SettleDeck();
        }
        routeChanged();
    }
    internal void Open(bool preview)
    {
        CancelCardSwitch();
        bool wasVisible = IsOpen;
        previewFront = preview && session.SongId is not null;
        SettleDeck();
        expanded = true;
        Overlay.Visibility = Visibility.Visible; Refresh(); Morph(true, !wasVisible);
        if (Microsoft.UI.Xaml.Input.FocusManager.FindFirstFocusableElement(front) is Control control) control.Focus(FocusState.Programmatic);
    }
    internal void Close()
    {
        if (!IsOpen || !expanded) return;
        CancelCardSwitch(settle: false);
        expanded = false; Morph(false);
    }
    private void OnRootSizeChanged(object sender, SizeChangedEventArgs args) => ResizeBar();
    private void ResizeBar()
    {
        title.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double limit = Math.Min(420, Math.Max(140, root.ActualWidth - 280));
        double target = Math.Clamp(Math.Ceiling(title.DesiredSize.Width + 30), session.SongId is null ? 48 : 120, limit);
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
        double sx = Math.Clamp(Bar.ActualWidth / Math.Max(1, deck.ActualWidth), .1, 1);
        double sy = Math.Clamp(Bar.ActualHeight / Math.Max(1, deck.ActualHeight), .05, 1);
        double dy = Bar.TransformToVisual(root).TransformPoint(default).Y - deck.TransformToVisual(root).TransformPoint(default).Y + morphTransform.TranslateY;
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
            deck.Opacity = opening ? 1 : 0;
            if (!opening)
            {
                Overlay.Visibility = Visibility.Collapsed; Bar.Opacity = 1; Bar.IsHitTestVisible = true;
                SettleDeck();
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
        Add(deck, "Opacity", opening && fresh ? 0 : deck.Opacity, opening ? 1 : 0);
        storyboard.Completed += (_, _) => Finish(); storyboard.Begin();
    }
    private void CancelCardSwitch(bool settle = true)
    {
        // Retain effective poses before Stop resets animation properties. Closing
        // can then shrink the moving deck in place rather than snapping it back.
        var poses = new[] { singingPose, previewPose }.Select(p => (p.ScaleX, p.ScaleY, p.TranslateY)).ToArray();
        double singingOpacity = singingCard.Opacity, previewOpacity = previewCard.Opacity;
        cardRevision++; cardSwitch?.Stop(); cardSwitch = null;
        if (settle) SettleDeck();
        else
        {
            for (int i = 0; i < poses.Length; i++)
            {
                var pose = i == 0 ? singingPose : previewPose;
                pose.ScaleX = poses[i].ScaleX; pose.ScaleY = poses[i].ScaleY; pose.TranslateY = poses[i].TranslateY;
            }
            singingCard.Opacity = singingOpacity; previewCard.Opacity = previewOpacity;
            singingCard.IsHitTestVisible = previewCard.IsHitTestVisible = false;
        }
    }
    private CompositeTransform Pose(Border card) => ReferenceEquals(card, singingCard) ? singingPose : previewPose;
    private void SettleDeck()
    {
        if (!IsOpen && session.SongId is null) previewFront = false;
        foreach (var card in new[] { singingCard, previewCard })
        {
            bool foreground = ReferenceEquals(card, front);
            var pose = Pose(card);
            pose.ScaleX = pose.ScaleY = foreground ? 1 : .94;
            pose.TranslateY = foreground ? 0 : 24;
            // Depth is conveyed by position/scale. Fading the whole acrylic card
            // would expose sharp text from the other card through its surface.
            card.Opacity = 1;
            card.IsHitTestVisible = foreground;
            Canvas.SetZIndex(card, foreground ? 2 : 1);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(card,
                foreground ? Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Content : Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        }
        previewCard.Visibility = session.SongId is not null || IsOpen && previewFront ? Visibility.Visible : Visibility.Collapsed;
    }
    private void AnimateCardSwitch(bool target, int direction)
    {
        if (!new Windows.UI.ViewManagement.UISettings().AnimationsEnabled) { previewFront = target; SettleDeck(); Refresh(); return; }
        long request = ++cardRevision;
        Border outgoing = front, incoming = back;
        CompositeTransform leavingPose = Pose(outgoing), enteringPose = Pose(incoming);
        outgoing.IsHitTestVisible = incoming.IsHitTestVisible = false;
        Bar.Focus(FocusState.Programmatic);
        double outgoingMid = direction > 0 ? 40 : -12;
        double incomingMid = direction > 0 ? -12 : 40;
        void Phase(bool second)
        {
            if (request != cardRevision) return;
            var storyboard = new Storyboard(); cardSwitch = storyboard;
            void Add(DependencyObject element, string property, double from, double to)
            {
                var animation = new DoubleAnimation { From = from, To = to, Duration = TimeSpan.FromMilliseconds(110), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } };
                Storyboard.SetTarget(animation, element); Storyboard.SetTargetProperty(animation, property); storyboard.Children.Add(animation);
            }
            foreach (string axis in new[] { "ScaleX", "ScaleY" })
            {
                Add(leavingPose, axis, second ? .97 : 1, second ? .94 : .97);
                Add(enteringPose, axis, second ? .97 : .94, second ? 1 : .97);
            }
            Add(leavingPose, "TranslateY", second ? outgoingMid : 0, second ? 24 : outgoingMid);
            Add(enteringPose, "TranslateY", second ? incomingMid : 24, second ? 0 : incomingMid);
            storyboard.Completed += (_, _) =>
            {
                if (request != cardRevision) return;
                storyboard.Stop();
                if (!second)
                {
                    leavingPose.ScaleX = leavingPose.ScaleY = enteringPose.ScaleX = enteringPose.ScaleY = .97;
                    leavingPose.TranslateY = outgoingMid; enteringPose.TranslateY = incomingMid;
                    outgoing.Opacity = incoming.Opacity = 1;
                    // Both real cards keep their children throughout; only depth changes.
                    previewFront = target;
                    Canvas.SetZIndex(incoming, 2); Canvas.SetZIndex(outgoing, 1);
                    Refresh();
                    if (request != cardRevision) return;
                    root.UpdateLayout(); Phase(true);
                }
                else
                {
                    cardSwitch = null; SettleDeck(); routeChanged();
                    if (FocusManager.FindFirstFocusableElement(front) is Control control) control.Focus(FocusState.Programmatic);
                }
            };
            routeChanged(); storyboard.Begin();
        }
        Phase(false);
    }
    private void Wheel(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;
        if (session.SongId is null || Transport.IsDragging) return;
        SwitchCard(e.GetCurrentPoint(deck).Properties.MouseWheelDelta);
    }
    internal void SwitchCard(int delta)
    {
        if (!IsOpen || !expanded || session.SongId is null || Transport.IsDragging || IsTransitioning) return;
        wheelRemainder += delta;
        if (Math.Abs(wheelRemainder) < 120) return;
        int direction = Math.Sign(wheelRemainder);
        wheelRemainder = 0; AnimateCardSwitch(!previewFront, direction);
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
    public void Dispose() { CancelCardSwitch(); marqueeTimer.Stop(); morph?.Stop(); widthTransition?.Stop(); root.SizeChanged -= OnRootSizeChanged; root.ActualThemeChanged -= OnHostThemeChanged; root.UnregisterPropertyChangedCallback(FrameworkElement.RequestedThemeProperty, hostThemeToken); session.Changed -= Refresh; Transport.Bind(null); }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern nint SendMessage(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern short GetKeyState(int key);
}
