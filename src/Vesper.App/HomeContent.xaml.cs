using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.System;
using Windows.UI;

namespace Vesper.App;

public sealed partial class HomeContent : UserControl
{
    private const double ResponsiveBreakpoint = 760;
    private const double UltraNarrowBreakpoint = 520;

    private readonly MixerVolumeControl vocalControl;
    private readonly MixerVolumeControl bgmControl;
    private readonly MixerLevelMeter micMeter;
    private readonly DispatcherTimer meterTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };

    private MonitoringPanel? controller;
    private bool syncingFromController;
    private bool homeActive;
    private bool compactLayout;
    private bool ultraNarrowLayout;
    private double smoothedLevel;
    private double peakHoldLevel;
    private int peakHoldTicks;

    internal event Action? StartSingingRequested;

    public HomeContent()
    {
        InitializeComponent();

        vocalControl = new MixerVolumeControl("VOCAL LEVEL", "人声音量", Color.FromArgb(255, 0x39, 0xC5, 0xBB));
        bgmControl = new MixerVolumeControl("BGM LEVEL", "伴奏音量", Color.FromArgb(255, 0x39, 0xC5, 0xBB));
        micMeter = new MixerLevelMeter();

        VocalVolumeHost.Children.Add(vocalControl);
        MicMeterHost.Children.Add(micMeter);
        BgmVolumeHost.Children.Add(bgmControl);

        vocalControl.ValueChanged += value =>
        {
            if (syncingFromController || controller is null) return;
            controller.VocalVolume = value;
        };
        bgmControl.ValueChanged += value =>
        {
            if (syncingFromController || controller is null) return;
            controller.BgmVolume = value;
        };

        meterTimer.Tick += OnMeterTick;
        Loaded += OnHomeLoaded;
        Unloaded += OnHomeUnloaded;
        ActualThemeChanged += (_, _) => ApplyVisualTheme();
        ApplyVisualTheme();
    }

    internal void Bind(MonitoringPanel monitoringController)
    {
        if (ReferenceEquals(controller, monitoringController))
        {
            SyncFromController();
            return;
        }
        if (controller is not null) controller.StateChanged -= OnControllerStateChanged;
        controller = monitoringController;
        controller.StateChanged += OnControllerStateChanged;
        SyncFromController();
        _ = controller.EnsureDevicesLoadedAsync();
    }

    internal void Unbind()
    {
        meterTimer.Stop();
        if (controller is not null)
        {
            controller.StateChanged -= OnControllerStateChanged;
            controller = null;
        }
    }

    internal void SetActive(bool active)
    {
        homeActive = active;
        controller?.SetHomePageActive(active);
        if (active) _ = controller?.EnsureDevicesLoadedAsync();
        SyncMeterTimer();
        SyncFromController();
    }

    internal void UpdateQueueStatus(int pendingCount, string? currentSongTitle, bool isLoading)
    {
        bool activeSession = !string.IsNullOrWhiteSpace(currentSongTitle);
        DisplayStageSummary.Text = activeSession
            ? $"演唱中 · {currentSongTitle}"
            : pendingCount > 0 ? $"待播 {pendingCount} 首" : "暂无待播曲目";
        DisplayQueueHint.Text = activeSession
            ? $"待播队列 {pendingCount} 首"
            : pendingCount > 0 ? $"下一首就绪（共 {pendingCount} 首）" : "进入等待舞台";

        QuickSingLabel.Text = isLoading ? "准备中…" : activeSession ? "返回演唱" : "开始演唱";
        QuickSingButton.IsEnabled = !isLoading;
    }

    private void OnHomeLoaded(object sender, RoutedEventArgs e)
    {
        ApplyVisualTheme();
        ApplyResponsiveLayout(ConsoleRoot.ActualWidth);
        SyncFromController();
        SyncMeterTimer();
    }

    private void OnHomeUnloaded(object sender, RoutedEventArgs e)
    {
        meterTimer.Stop();
    }

    private void OnConsoleSizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyResponsiveLayout(e.NewSize.Width);
    }

    private void ApplyResponsiveLayout(double width)
    {
        if (width <= 0) return;
        bool compact = width < ResponsiveBreakpoint;
        bool ultraNarrow = width < UltraNarrowBreakpoint;

        if (compact != compactLayout)
        {
            compactLayout = compact;
            vocalControl.CompactMode = compact;
            bgmControl.CompactMode = compact;
            micMeter.CompactMode = compact;

            if (compact)
            {
                ChannelsGrid.ColumnDefinitions[1].Width = new GridLength(0);
                ChannelsGrid.ColumnSpacing = 0;
                Grid.SetRow(BgmChannelCard, 1);
                Grid.SetColumn(BgmChannelCard, 0);

                DisplayDivider1.Visibility = Visibility.Collapsed;
                DisplayDivider2.Visibility = Visibility.Collapsed;
                DisplayScreenGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
                DisplayScreenGrid.ColumnDefinitions[1].Width = new GridLength(0);
                DisplayScreenGrid.ColumnDefinitions[2].Width = new GridLength(0);
                DisplayScreenGrid.ColumnDefinitions[3].Width = new GridLength(0);
                DisplayScreenGrid.ColumnDefinitions[4].Width = new GridLength(0);
                DisplayScreenGrid.ColumnSpacing = 0;
                Grid.SetRow(DisplayStatusCell, 0); Grid.SetColumn(DisplayStatusCell, 0);
                Grid.SetRow(DisplayRoutingCell, 1); Grid.SetColumn(DisplayRoutingCell, 0);
                Grid.SetRow(DisplayActionCell, 2); Grid.SetColumn(DisplayActionCell, 0);
                DisplayActionCell.HorizontalAlignment = HorizontalAlignment.Stretch;
                QuickSingButton.HorizontalAlignment = HorizontalAlignment.Stretch;
            }
            else
            {
                ChannelsGrid.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
                ChannelsGrid.ColumnSpacing = 16;
                Grid.SetRow(BgmChannelCard, 0);
                Grid.SetColumn(BgmChannelCard, 1);

                DisplayDivider1.Visibility = Visibility.Visible;
                DisplayDivider2.Visibility = Visibility.Visible;
                DisplayScreenGrid.ColumnDefinitions[0].Width = new GridLength(1.2, GridUnitType.Star);
                DisplayScreenGrid.ColumnDefinitions[1].Width = GridLength.Auto;
                DisplayScreenGrid.ColumnDefinitions[2].Width = new GridLength(1.2, GridUnitType.Star);
                DisplayScreenGrid.ColumnDefinitions[3].Width = GridLength.Auto;
                DisplayScreenGrid.ColumnDefinitions[4].Width = GridLength.Auto;
                DisplayScreenGrid.ColumnSpacing = 18;
                Grid.SetRow(DisplayStatusCell, 0); Grid.SetColumn(DisplayStatusCell, 0);
                Grid.SetRow(DisplayRoutingCell, 0); Grid.SetColumn(DisplayRoutingCell, 2);
                Grid.SetRow(DisplayActionCell, 0); Grid.SetColumn(DisplayActionCell, 4);
                DisplayActionCell.HorizontalAlignment = HorizontalAlignment.Right;
                QuickSingButton.HorizontalAlignment = HorizontalAlignment.Center;
            }
        }

        if (ultraNarrow != ultraNarrowLayout)
        {
            ultraNarrowLayout = ultraNarrow;
            if (ultraNarrow)
            {
                DeviceSelectorsGrid.ColumnDefinitions[1].Width = new GridLength(0);
                DeviceSelectorsGrid.ColumnSpacing = 0;
                Grid.SetRow(OutputDeviceBox, 1);
                Grid.SetColumn(OutputDeviceBox, 0);

                BgmWellGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
                BgmWellGrid.ColumnDefinitions[1].Width = new GridLength(0);
                BgmWellGrid.ColumnSpacing = 0;
                Grid.SetRow(DspRackPanel, 1);
                Grid.SetColumn(DspRackPanel, 0);
            }
            else
            {
                DeviceSelectorsGrid.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
                DeviceSelectorsGrid.ColumnSpacing = 12;
                Grid.SetRow(OutputDeviceBox, 0);
                Grid.SetColumn(OutputDeviceBox, 1);

                BgmWellGrid.ColumnDefinitions[0].Width = GridLength.Auto;
                BgmWellGrid.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
                BgmWellGrid.ColumnSpacing = 14;
                Grid.SetRow(DspRackPanel, 0);
                Grid.SetColumn(DspRackPanel, 1);
            }
        }
    }

    private void OnControllerStateChanged()
    {
        SyncFromController();
        SyncMeterTimer();
    }

    private void SyncFromController()
    {
        if (controller is null) return;
        syncingFromController = true;
        try
        {
            if (!ReferenceEquals(InputDeviceBox.ItemsSource, controller.InputDevices))
                InputDeviceBox.ItemsSource = controller.InputDevices;
            if (!ReferenceEquals(OutputDeviceBox.ItemsSource, controller.OutputDevices))
                OutputDeviceBox.ItemsSource = controller.OutputDevices;

            InputDeviceBox.SelectedItem = controller.SelectedInput;
            OutputDeviceBox.SelectedItem = controller.SelectedOutput;
            InputDeviceBox.IsEnabled = !controller.IsEnumerating && !controller.IsStarting;
            OutputDeviceBox.IsEnabled = !controller.IsEnumerating && !controller.IsStarting;

            vocalControl.Value = controller.VocalVolume;
            bgmControl.Value = controller.BgmVolume;
            micMeter.Segmented = controller.SegmentedLevelMeter;

            bool monitoringActive = controller.IsMonitoringActive;
            bool exclusive = controller.SelectedMode == MonitoringMode.Exclusive;
            MonitoringToggleButton.IsEnabled = controller.CanToggleMonitoring;
            RefreshDevicesButton.IsEnabled = controller.CanRefreshDevices;
            LowLatencyModeButton.IsEnabled = !controller.IsEnumerating && !controller.IsStarting;
            ExclusiveModeButton.IsEnabled = !controller.IsEnumerating && !controller.IsStarting;

            MonitoringToggleLabel.Text = monitoringActive ? "关闭实时返听"
                : controller.IsStarting ? "正在准备返听…" : "开启实时返听";
            MonitoringButtonLed.Fill = new SolidColorBrush(monitoringActive
                ? Color.FromArgb(255, 0x22, 0xC5, 0x5E)
                : Color.FromArgb(255, 0xEF, 0x44, 0x44));
            MonitoringStatusText.Text = controller.StatusText;
            ModeUsageText.Text = controller.UsageText;

            UpdateModeButtons(exclusive);
            UpdateDisplayScreen(monitoringActive, exclusive);
            UpdateMonitoringButtonStyle(monitoringActive);

            micMeter.Update(smoothedLevel, peakHoldLevel, monitoringActive, controller.IsMeterCapturing);
        }
        finally
        {
            syncingFromController = false;
        }
    }

    private void UpdateDisplayScreen(bool monitoringActive, bool exclusive)
    {
        if (controller is null) return;
        bool capturing = controller.IsMeterCapturing;
        if (monitoringActive)
        {
            StatusLed.Fill = new SolidColorBrush(Color.FromArgb(255, 0x22, 0xC5, 0x5E));
            DisplayStatusCode.Text = "LIVE MONITORING";
            DisplayStatusDetail.Text = "实时耳返已开启 · 人声实时输出中";
        }
        else if (controller.IsEnumerating || controller.IsStarting)
        {
            StatusLed.Fill = new SolidColorBrush(Color.FromArgb(255, 0xF5, 0x9E, 0x0B));
            DisplayStatusCode.Text = "INITIALIZING";
            DisplayStatusDetail.Text = controller.IsEnumerating ? "正在扫描音频设备…" : "正在准备低延迟引擎…";
        }
        else if (capturing)
        {
            StatusLed.Fill = new SolidColorBrush(Color.FromArgb(255, 0xEF, 0x44, 0x44));
            DisplayStatusCode.Text = "MIC CHECK";
            DisplayStatusDetail.Text = "试麦采集中 · 耳返未开（无声输出）";
        }
        else
        {
            StatusLed.Fill = new SolidColorBrush(controller.SelectedInput is null
                ? Color.FromArgb(255, 0xF5, 0x9E, 0x0B)
                : Color.FromArgb(255, 0xEF, 0x44, 0x44));
            DisplayStatusCode.Text = controller.SelectedInput is null ? "NO INPUT" : "STANDBY";
            DisplayStatusDetail.Text = controller.StatusText;
        }

        DisplayModeLabel.Text = exclusive ? "独占模式" : "共享低延迟";
        if (controller.RunningInfo is { } info)
        {
            double inMs = info.InputPeriod * 1000d / info.InputRate;
            double outMs = info.OutputPeriod * 1000d / info.OutputRate;
            DisplayPeriodText.Text = $"ENGINE: IN {inMs:F1}ms / OUT {outMs:F1}ms";
        }
        else
        {
            DisplayPeriodText.Text = exclusive
                ? "ENGINE: EXCLUSIVE DRY MON"
                : capturing ? "ENGINE: MIC METER ACTIVE" : "ENGINE: STANDBY";
        }

        DisplayInputName.Text = controller.SelectedInput?.Name ?? "未选择麦克风";
        DisplayOutputName.Text = controller.SelectedOutput?.Name ?? "未选择输出设备";
    }

    private void UpdateModeButtons(bool exclusive)
    {
        bool dark = ActualTheme != ElementTheme.Light;
        var activeBg = new SolidColorBrush(Color.FromArgb(42, 0x39, 0xC5, 0xBB));
        var activeBorder = new SolidColorBrush(Color.FromArgb(255, 0x39, 0xC5, 0xBB));
        var activeFg = new SolidColorBrush(dark ? Color.FromArgb(255, 0xEC, 0xFD, 0xFB) : Color.FromArgb(255, 0x0F, 0x4C, 0x48));
        var inactiveBg = new SolidColorBrush(dark ? Color.FromArgb(255, 0x25, 0x28, 0x30) : Color.FromArgb(255, 0xDF, 0xE4, 0xEC));
        var inactiveBorder = new SolidColorBrush(dark ? Color.FromArgb(255, 0x35, 0x39, 0x42) : Color.FromArgb(255, 0xC5, 0xCC, 0xD6));
        var inactiveFg = new SolidColorBrush(dark ? Color.FromArgb(255, 0xB6, 0xBF, 0xCE) : Color.FromArgb(255, 0x3C, 0x45, 0x54));

        ApplyButtonVisual(LowLatencyModeButton, !exclusive ? activeBg : inactiveBg, !exclusive ? activeBorder : inactiveBorder,
            !exclusive ? activeFg : inactiveFg, !exclusive ? FontWeights.SemiBold : FontWeights.Normal);
        ApplyButtonVisual(ExclusiveModeButton, exclusive ? activeBg : inactiveBg, exclusive ? activeBorder : inactiveBorder,
            exclusive ? activeFg : inactiveFg, exclusive ? FontWeights.SemiBold : FontWeights.Normal);
    }

    private void UpdateMonitoringButtonStyle(bool active)
    {
        bool dark = ActualTheme != ElementTheme.Light;
        var bg = new SolidColorBrush(active
            ? Color.FromArgb(36, 0x22, 0xC5, 0x5E)
            : dark ? Color.FromArgb(255, 0x25, 0x28, 0x30) : Color.FromArgb(255, 0xDF, 0xE4, 0xEC));
        var border = new SolidColorBrush(active
            ? Color.FromArgb(255, 0x22, 0xC5, 0x5E)
            : dark ? Color.FromArgb(255, 0x3A, 0x3F, 0x4B) : Color.FromArgb(255, 0xC0, 0xC7, 0xD2));
        var fg = new SolidColorBrush(active
            ? (dark ? Color.FromArgb(255, 0xDC, 0xFC, 0xE7) : Color.FromArgb(255, 0x14, 0x53, 0x2D))
            : (dark ? Color.FromArgb(255, 0xE2, 0xE8, 0xF0) : Color.FromArgb(255, 0x1E, 0x29, 0x3B)));
        ApplyButtonVisual(MonitoringToggleButton, bg, border, fg, FontWeights.SemiBold);
    }

    private static void ApplyButtonVisual(Button button, Brush background, Brush border, Brush foreground, Windows.UI.Text.FontWeight weight)
    {
        button.Background = background;
        button.BorderBrush = border;
        button.Foreground = foreground;
        button.FontWeight = weight;
    }

    private void ApplyVisualTheme()
    {
        bool dark = ActualTheme != ElementTheme.Light;
        vocalControl.ApplyTheme(dark);
        bgmControl.ApplyTheme(dark);
        micMeter.ApplyTheme(dark);
        UpdateModeButtons(controller?.SelectedMode == MonitoringMode.Exclusive);
        UpdateMonitoringButtonStyle(controller?.IsMonitoringActive == true);
    }

    private void SyncMeterTimer()
    {
        bool shouldRun = IsLoaded && homeActive && controller is not null
            && (controller.IsMeterCapturing || smoothedLevel > 0.002 || peakHoldLevel > 0.002);
        if (shouldRun && !meterTimer.IsEnabled) meterTimer.Start();
        else if (!shouldRun && meterTimer.IsEnabled)
        {
            meterTimer.Stop();
            smoothedLevel = 0;
            peakHoldLevel = 0;
            peakHoldTicks = 0;
            micMeter.Update(0, 0, controller?.IsMonitoringActive == true, controller?.IsMeterCapturing == true);
        }
    }

    private void OnMeterTick(object? sender, object e)
    {
        if (controller is null) { meterTimer.Stop(); return; }
        bool capturing = controller.IsMeterCapturing;
        double raw = homeActive && capturing ? controller.ReadNormalizedLevel() : 0;
        smoothedLevel = raw >= smoothedLevel ? raw : Math.Max(raw, smoothedLevel * 0.84);
        if (smoothedLevel < 0.004) smoothedLevel = 0;

        if (smoothedLevel >= peakHoldLevel)
        {
            peakHoldLevel = smoothedLevel;
            peakHoldTicks = 18;
        }
        else if (peakHoldTicks > 0)
        {
            peakHoldTicks--;
        }
        else
        {
            peakHoldLevel = Math.Max(0, peakHoldLevel - 0.035);
        }

        micMeter.Update(smoothedLevel, peakHoldLevel, controller.IsMonitoringActive, capturing);

        if (!capturing && smoothedLevel <= 0 && peakHoldLevel <= 0) meterTimer.Stop();
    }

    private void OnInputDeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (syncingFromController || controller is null) return;
        if (InputDeviceBox.SelectedItem is MonitoringDevice device) controller.SelectedInput = device;
    }

    private void OnOutputDeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (syncingFromController || controller is null) return;
        if (OutputDeviceBox.SelectedItem is MonitoringDevice device) controller.SelectedOutput = device;
    }

    private async void OnRefreshDevicesClicked(object sender, RoutedEventArgs e)
    {
        if (controller is not null) await controller.RefreshDevicesAsync();
    }

    private async void OnMonitoringToggleClicked(object sender, RoutedEventArgs e)
    {
        if (controller is not null) await controller.ToggleMonitoringAsync();
    }

    private void OnLowLatencyModeClicked(object sender, RoutedEventArgs e)
    {
        if (controller is not null) controller.SelectedMode = MonitoringMode.LowLatency;
    }

    private void OnExclusiveModeClicked(object sender, RoutedEventArgs e)
    {
        if (controller is not null) controller.SelectedMode = MonitoringMode.Exclusive;
    }

    private void OnQuickSingClicked(object sender, RoutedEventArgs e) => StartSingingRequested?.Invoke();

    // 响应式物理音量推子（宽窗）与旋转旋钮（窄窗）
    private sealed class MixerVolumeControl : Grid
    {
        private const double FaderTravel = 164;
        private const double FaderTopOffset = 12;

        private readonly string titleText;
        private readonly Color accentColor;
        private readonly TextBlock titleBlock = new() { FontSize = 11, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
        private readonly TextBlock valueReadout = new() { FontSize = 12, FontFamily = new FontFamily("Consolas"), FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
        private readonly Border readoutPill = new() { CornerRadius = new CornerRadius(5), Padding = new Thickness(8, 3, 8, 3), HorizontalAlignment = HorizontalAlignment.Center, BorderThickness = new Thickness(1) };

        // Fader parts
        private readonly Grid faderHost = new() { Width = 92, Height = 192 };
        private readonly Canvas faderTrackCanvas = new() { Width = 44, Height = 188, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Background = new SolidColorBrush(Colors.Transparent) };
        private readonly Border faderGroove = new() { Width = 6, Height = FaderTravel, CornerRadius = new CornerRadius(3) };
        private readonly Border faderFill = new() { Width = 6, CornerRadius = new CornerRadius(3) };
        private readonly Border faderCap = new() { Width = 34, Height = 20, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1) };
        private readonly Rectangle faderCapLine = new() { Width = 22, Height = 2, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        private readonly List<TextBlock> scaleLabels = [];
        private readonly List<Rectangle> tickMarks = [];

        // Knob parts
        private readonly Grid knobHost = new() { Width = 98, Height = 98, Visibility = Visibility.Collapsed, Background = new SolidColorBrush(Colors.Transparent) };
        private readonly Microsoft.UI.Xaml.Shapes.Path knobTrackArc = new() { StrokeThickness = 4, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        private readonly Microsoft.UI.Xaml.Shapes.Path knobValueArc = new() { StrokeThickness = 4, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        private readonly ArcSegment knobValueSegment = new() { Size = new Size(39, 39), SweepDirection = SweepDirection.Clockwise };
        private readonly Ellipse knobOuter = new() { Width = 56, Height = 56, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, StrokeThickness = 1.5 };
        private readonly Ellipse knobInner = new() { Width = 46, Height = 46, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        private readonly Grid knobRotor = new() { Width = 56, Height = 56, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        private readonly RotateTransform knobRotate = new() { CenterX = 28, CenterY = 28 };
        private readonly Rectangle knobPointer = new() { Width = 3, Height = 12, RadiusX = 1.5, RadiusY = 1.5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 6, 0, 0) };

        private double value = 50;
        private bool compact;
        private bool dragging;
        private double knobDragStartY;
        private double knobDragStartValue;

        internal event Action<double>? ValueChanged;

        internal double Value
        {
            get => value;
            set
            {
                double clamped = Math.Clamp(double.IsFinite(value) ? value : 0, 0, 100);
                if (Math.Abs(this.value - clamped) < 0.01) return;
                this.value = clamped;
                UpdateVisual();
            }
        }

        internal bool CompactMode
        {
            get => compact;
            set
            {
                if (compact == value) return;
                compact = value;
                faderHost.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
                knobHost.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
                UpdateVisual();
            }
        }

        internal MixerVolumeControl(string shortLabel, string accessibleName, Color accentColor)
        {
            titleText = shortLabel;
            this.accentColor = accentColor;
            IsTabStop = true;
            UseSystemFocusVisuals = true;
            AutomationProperties.SetName(this, accessibleName);

            RowSpacing = 8;
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            titleBlock.Text = titleText;
            Children.Add(titleBlock);

            BuildFader();
            BuildKnob();

            var body = new Grid { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            body.Children.Add(faderHost);
            body.Children.Add(knobHost);
            SetRow(body, 1);
            Children.Add(body);

            readoutPill.Child = valueReadout;
            SetRow(readoutPill, 2);
            Children.Add(readoutPill);

            KeyDown += OnControlKeyDown;
            PointerWheelChanged += OnControlWheelChanged;
            ApplyTheme(true);
            UpdateVisual();
        }

        private void BuildFader()
        {
            faderHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
            faderHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });

            var scaleCanvas = new Canvas { Width = 36, Height = 188 };
            for (int step = 0; step <= 10; step++)
            {
                double y = FaderTopOffset + FaderTravel * (1.0 - step / 10.0);
                bool major = step % 5 == 0 || step == 2 || step == 8;
                var tick = new Rectangle { Width = major ? 9 : 5, Height = 1 };
                Canvas.SetLeft(tick, 36 - tick.Width - 2);
                Canvas.SetTop(tick, y);
                scaleCanvas.Children.Add(tick);
                tickMarks.Add(tick);

                if (step is 0 or 2 or 5 or 8 or 10)
                {
                    var label = new TextBlock { Text = $"{step * 10}", FontSize = 9, FontFamily = new FontFamily("Consolas") };
                    Canvas.SetLeft(label, 0);
                    Canvas.SetTop(label, y - 6);
                    scaleCanvas.Children.Add(label);
                    scaleLabels.Add(label);
                }
            }
            faderHost.Children.Add(scaleCanvas);

            for (int step = 0; step <= 10; step++)
            {
                double y = FaderTopOffset + FaderTravel * (1.0 - step / 10.0);
                bool major = step % 5 == 0;
                var rightTick = new Rectangle { Width = major ? 7 : 4, Height = 1 };
                Canvas.SetLeft(rightTick, 30);
                Canvas.SetTop(rightTick, y);
                faderTrackCanvas.Children.Add(rightTick);
                tickMarks.Add(rightTick);
            }

            Canvas.SetLeft(faderGroove, 19);
            Canvas.SetTop(faderGroove, FaderTopOffset);
            faderTrackCanvas.Children.Add(faderGroove);

            faderFill.Background = new SolidColorBrush(accentColor);
            Canvas.SetLeft(faderFill, 19);
            faderTrackCanvas.Children.Add(faderFill);

            faderCap.Child = faderCapLine;
            Canvas.SetLeft(faderCap, 5);
            faderTrackCanvas.Children.Add(faderCap);

            SetColumn(faderTrackCanvas, 1);
            faderHost.Children.Add(faderTrackCanvas);

            faderTrackCanvas.PointerPressed += OnFaderPointerPressed;
            faderTrackCanvas.PointerMoved += OnFaderPointerMoved;
            faderTrackCanvas.PointerReleased += OnFaderPointerReleased;
            faderTrackCanvas.PointerCanceled += OnFaderPointerReleased;
            faderTrackCanvas.PointerCaptureLost += OnFaderPointerReleased;
        }

        private void BuildKnob()
        {
            const double center = 49;
            const double radius = 39;

            var tickCanvas = new Canvas { Width = 98, Height = 98, IsHitTestVisible = false };
            for (int i = 0; i <= 10; i++)
            {
                double deg = 135.0 + 27.0 * i;
                double rad = deg * Math.PI / 180.0;
                double rOuter = 47;
                double rInner = i % 5 == 0 ? 41 : 43.5;
                var line = new Line
                {
                    X1 = center + rInner * Math.Cos(rad),
                    Y1 = center + rInner * Math.Sin(rad),
                    X2 = center + rOuter * Math.Cos(rad),
                    Y2 = center + rOuter * Math.Sin(rad),
                    StrokeThickness = i % 5 == 0 ? 1.5 : 1
                };
                tickCanvas.Children.Add(line);
            }
            knobHost.Children.Add(tickCanvas);

            double startRad = 135.0 * Math.PI / 180.0;
            double endRad = 405.0 * Math.PI / 180.0;
            Point startPt = new(center + radius * Math.Cos(startRad), center + radius * Math.Sin(startRad));
            Point endPt = new(center + radius * Math.Cos(endRad), center + radius * Math.Sin(endRad));

            var trackFigure = new PathFigure { StartPoint = startPt };
            trackFigure.Segments.Add(new ArcSegment
            {
                Point = endPt,
                Size = new Size(radius, radius),
                IsLargeArc = true,
                SweepDirection = SweepDirection.Clockwise
            });
            var trackGeometry = new PathGeometry();
            trackGeometry.Figures.Add(trackFigure);
            knobTrackArc.Data = trackGeometry;
            knobHost.Children.Add(knobTrackArc);

            var valueFigure = new PathFigure { StartPoint = startPt };
            valueFigure.Segments.Add(knobValueSegment);
            var valueGeometry = new PathGeometry();
            valueGeometry.Figures.Add(valueFigure);
            knobValueArc.Data = valueGeometry;
            knobValueArc.Stroke = new SolidColorBrush(accentColor);
            knobHost.Children.Add(knobValueArc);

            knobHost.Children.Add(knobOuter);
            knobHost.Children.Add(knobInner);

            knobRotor.RenderTransform = knobRotate;
            knobRotor.Children.Add(knobPointer);
            knobHost.Children.Add(knobRotor);

            knobHost.PointerPressed += OnKnobPointerPressed;
            knobHost.PointerMoved += OnKnobPointerMoved;
            knobHost.PointerReleased += OnKnobPointerReleased;
            knobHost.PointerCanceled += OnKnobPointerReleased;
            knobHost.PointerCaptureLost += OnKnobPointerReleased;
        }

        internal void ApplyTheme(bool dark)
        {
            titleBlock.Foreground = new SolidColorBrush(dark ? Color.FromArgb(255, 0x9A, 0xA3, 0xB2) : Color.FromArgb(255, 0x56, 0x60, 0x70));
            readoutPill.Background = new SolidColorBrush(dark ? Color.FromArgb(255, 0x10, 0x13, 0x18) : Color.FromArgb(255, 0xE2, 0xE6, 0xED));
            readoutPill.BorderBrush = new SolidColorBrush(dark ? Color.FromArgb(255, 0x26, 0x2C, 0x36) : Color.FromArgb(255, 0xB6, 0xBE, 0xCA));
            valueReadout.Foreground = new SolidColorBrush(dark ? Color.FromArgb(255, 0xE2, 0xE8, 0xF0) : Color.FromArgb(255, 0x1E, 0x29, 0x3B));

            faderGroove.Background = new SolidColorBrush(dark ? Color.FromArgb(255, 0x0D, 0x0F, 0x13) : Color.FromArgb(255, 0xB8, 0xBF, 0xCB));
            faderCap.Background = new SolidColorBrush(dark ? Color.FromArgb(255, 0x38, 0x3C, 0x46) : Color.FromArgb(255, 0xFB, 0xFC, 0xFD));
            faderCap.BorderBrush = new SolidColorBrush(dark ? Color.FromArgb(255, 0x60, 0x67, 0x76) : Color.FromArgb(255, 0x8F, 0x98, 0xA8));
            faderCapLine.Fill = new SolidColorBrush(dark ? Color.FromArgb(255, 0xE2, 0xE8, 0xF0) : Color.FromArgb(255, 0x2B, 0x30, 0x3A));

            var tickBrush = new SolidColorBrush(dark ? Color.FromArgb(255, 0x54, 0x5B, 0x6B) : Color.FromArgb(255, 0x8A, 0x93, 0xA2));
            foreach (var tick in tickMarks) tick.Fill = tickBrush;
            var labelBrush = new SolidColorBrush(dark ? Color.FromArgb(255, 0x7A, 0x82, 0x94) : Color.FromArgb(255, 0x56, 0x60, 0x70));
            foreach (var label in scaleLabels) label.Foreground = labelBrush;

            knobTrackArc.Stroke = new SolidColorBrush(dark ? Color.FromArgb(255, 0x2A, 0x2E, 0x38) : Color.FromArgb(255, 0xC2, 0xC8, 0xD4));
            knobOuter.Fill = new SolidColorBrush(dark ? Color.FromArgb(255, 0x26, 0x29, 0x31) : Color.FromArgb(255, 0xE6, 0xEA, 0xF0));
            knobOuter.Stroke = new SolidColorBrush(dark ? Color.FromArgb(255, 0x4E, 0x54, 0x63) : Color.FromArgb(255, 0x9B, 0xA3, 0xB2));
            knobInner.Fill = new SolidColorBrush(dark ? Color.FromArgb(255, 0x31, 0x35, 0x3F) : Color.FromArgb(255, 0xFA, 0xFB, 0xFC));
            knobPointer.Fill = new SolidColorBrush(accentColor);

            if (knobHost.Children.FirstOrDefault() is Canvas tickCanvas)
                foreach (var line in tickCanvas.Children.OfType<Line>()) line.Stroke = tickBrush;
        }

        private void UpdateVisual()
        {
            double t = Math.Clamp(value / 100.0, 0, 1);
            valueReadout.Text = $"{Math.Round(value):0}%";

            if (!compact)
            {
                double fillHeight = FaderTravel * t;
                faderFill.Height = fillHeight;
                Canvas.SetTop(faderFill, FaderTopOffset + FaderTravel - fillHeight);
                double capCenterY = FaderTopOffset + FaderTravel * (1.0 - t);
                Canvas.SetTop(faderCap, capCenterY - faderCap.Height / 2);
            }
            else
            {
                knobRotate.Angle = -135.0 + 270.0 * t;
                if (t <= 0.005)
                {
                    knobValueArc.Visibility = Visibility.Collapsed;
                }
                else
                {
                    knobValueArc.Visibility = Visibility.Visible;
                    double sweepDeg = 270.0 * t;
                    double endRad = (135.0 + sweepDeg) * Math.PI / 180.0;
                    knobValueSegment.Point = new Point(49 + 39 * Math.Cos(endRad), 49 + 39 * Math.Sin(endRad));
                    knobValueSegment.IsLargeArc = sweepDeg > 180.0;
                }
            }
        }

        private void SetUserValue(double next)
        {
            double clamped = Math.Clamp(Math.Round(next), 0, 100);
            if (Math.Abs(value - clamped) < 0.01) return;
            value = clamped;
            UpdateVisual();
            ValueChanged?.Invoke(value);
        }

        private void OnFaderPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            Focus(FocusState.Pointer);
            dragging = faderTrackCanvas.CapturePointer(e.Pointer);
            UpdateFaderFromPoint(e.GetCurrentPoint(faderTrackCanvas).Position.Y);
            e.Handled = true;
        }

        private void OnFaderPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!dragging) return;
            UpdateFaderFromPoint(e.GetCurrentPoint(faderTrackCanvas).Position.Y);
            e.Handled = true;
        }

        private void OnFaderPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!dragging) return;
            dragging = false;
            faderTrackCanvas.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
        }

        private void UpdateFaderFromPoint(double y)
        {
            double ratio = 1.0 - (y - FaderTopOffset) / FaderTravel;
            SetUserValue(ratio * 100.0);
        }

        private void OnKnobPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            Focus(FocusState.Pointer);
            dragging = knobHost.CapturePointer(e.Pointer);
            knobDragStartY = e.GetCurrentPoint(knobHost).Position.Y;
            knobDragStartValue = value;
            e.Handled = true;
        }

        private void OnKnobPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!dragging) return;
            double currentY = e.GetCurrentPoint(knobHost).Position.Y;
            SetUserValue(knobDragStartValue + (knobDragStartY - currentY) / 1.4);
            e.Handled = true;
        }

        private void OnKnobPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!dragging) return;
            dragging = false;
            knobHost.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
        }

        private void OnControlWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            int delta = e.GetCurrentPoint(this).Properties.MouseWheelDelta;
            SetUserValue(value + (delta > 0 ? 4 : -4));
            e.Handled = true;
        }

        private void OnControlKeyDown(object sender, KeyRoutedEventArgs e)
        {
            double? next = e.Key switch
            {
                VirtualKey.Up or VirtualKey.Right => value + 2,
                VirtualKey.Down or VirtualKey.Left => value - 2,
                VirtualKey.PageUp => value + 10,
                VirtualKey.PageDown => value - 10,
                VirtualKey.Home => 0,
                VirtualKey.End => 100,
                _ => null
            };
            if (next is { } target) { SetUserValue(target); e.Handled = true; }
        }
    }

    // 实时麦克风电平灯柱：支持未开耳返红色／开启耳返绿色，以及分段 LED／连续平滑双样式
    private sealed class MixerLevelMeter : Grid
    {
        private const int SegmentCount = 16;

        private readonly Ellipse stateDot = new() { Width = 7, Height = 7, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock stateLabel = new() { FontSize = 11, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        private readonly Border meterWell = new() { Width = 38, Height = 188, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Padding = new Thickness(5) };
        private readonly Grid segmentedGrid = new() { RowSpacing = 2 };
        private readonly Border[] segments = new Border[SegmentCount];
        private readonly Grid smoothHost = new() { Visibility = Visibility.Collapsed };
        private readonly Border smoothBar = new() { VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Stretch, CornerRadius = new CornerRadius(3), Height = 0 };
        private readonly Border peakLine = new() { VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Stretch, Height = 2, CornerRadius = new CornerRadius(1), Visibility = Visibility.Collapsed };
        private readonly Canvas scaleCanvas = new() { Width = 28, Height = 188 };
        private readonly List<TextBlock> dbLabels = [];
        private readonly Border dbPill = new() { CornerRadius = new CornerRadius(5), Padding = new Thickness(8, 3, 8, 3), HorizontalAlignment = HorizontalAlignment.Center, BorderThickness = new Thickness(1) };
        private readonly TextBlock dbReadout = new() { Text = "-∞ dB", FontSize = 12, FontFamily = new FontFamily("Consolas"), FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };

        private readonly SolidColorBrush greenPrimaryBrush = new(Color.FromArgb(255, 0x22, 0xC5, 0x5E));
        private readonly SolidColorBrush greenTopBrush = new(Color.FromArgb(255, 0x4A, 0xDE, 0x80));
        private readonly SolidColorBrush greenDeepBrush = new(Color.FromArgb(255, 0x15, 0x80, 0x3D));
        private readonly SolidColorBrush greenPeakBrush = new(Color.FromArgb(255, 0x86, 0xEF, 0xAC));
        private readonly SolidColorBrush greenUnlitBrush = new(Color.FromArgb(255, 0x13, 0x26, 0x1B));
        private readonly SolidColorBrush redPrimaryBrush = new(Color.FromArgb(255, 0xEF, 0x44, 0x44));
        private readonly SolidColorBrush redTopBrush = new(Color.FromArgb(255, 0xF8, 0x71, 0x71));
        private readonly SolidColorBrush redDeepBrush = new(Color.FromArgb(255, 0xB9, 0x1C, 0x1C));
        private readonly SolidColorBrush redPeakBrush = new(Color.FromArgb(255, 0xFC, 0xA5, 0xA5));
        private readonly SolidColorBrush redUnlitBrush = new(Color.FromArgb(255, 0x29, 0x14, 0x18));
        private readonly LinearGradientBrush greenSmoothBrush = CreateSmoothBrush(
            Color.FromArgb(255, 0x15, 0x80, 0x3D), Color.FromArgb(255, 0x22, 0xC5, 0x5E), Color.FromArgb(255, 0x4A, 0xDE, 0x80));
        private readonly LinearGradientBrush redSmoothBrush = CreateSmoothBrush(
            Color.FromArgb(255, 0xB9, 0x1C, 0x1C), Color.FromArgb(255, 0xEF, 0x44, 0x44), Color.FromArgb(255, 0xF8, 0x71, 0x71));

        private bool segmented = true;
        private bool compact;
        private bool isDark = true;
        private double lastLevel, lastPeak;
        private bool lastMonitoring, lastCapturing;

        private static LinearGradientBrush CreateSmoothBrush(Color deep, Color primary, Color top)
        {
            var gradient = new LinearGradientBrush { StartPoint = new Point(0, 1), EndPoint = new Point(0, 0) };
            gradient.GradientStops.Add(new GradientStop { Color = deep, Offset = 0 });
            gradient.GradientStops.Add(new GradientStop { Color = primary, Offset = 0.65 });
            gradient.GradientStops.Add(new GradientStop { Color = top, Offset = 1 });
            return gradient;
        }

        internal bool Segmented
        {
            get => segmented;
            set
            {
                if (segmented == value) return;
                segmented = value;
                segmentedGrid.Visibility = segmented ? Visibility.Visible : Visibility.Collapsed;
                smoothHost.Visibility = segmented ? Visibility.Collapsed : Visibility.Visible;
                Update(lastLevel, lastPeak, lastMonitoring, lastCapturing);
            }
        }

        internal bool CompactMode
        {
            get => compact;
            set
            {
                if (compact == value) return;
                compact = value;
                double targetHeight = compact ? 132 : 188;
                meterWell.Height = targetHeight;
                scaleCanvas.Height = targetHeight;
                LayoutScaleLabels(targetHeight);
                Update(lastLevel, lastPeak, lastMonitoring, lastCapturing);
            }
        }

        internal MixerLevelMeter()
        {
            RowSpacing = 8;
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            AutomationProperties.SetName(this, "实时麦克风电平表");

            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center };
            header.Children.Add(stateDot);
            header.Children.Add(stateLabel);
            Children.Add(header);

            for (int i = 0; i < SegmentCount; i++)
            {
                segmentedGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                var led = new Border { CornerRadius = new CornerRadius(2) };
                SetRow(led, SegmentCount - 1 - i);
                segmentedGrid.Children.Add(led);
                segments[i] = led;
            }

            smoothHost.Children.Add(smoothBar);
            smoothHost.Children.Add(peakLine);

            var wellContent = new Grid();
            wellContent.Children.Add(segmentedGrid);
            wellContent.Children.Add(smoothHost);
            meterWell.Child = wellContent;

            foreach (string labelText in new[] { "0", "-12", "-24", "-36", "-52" })
            {
                var text = new TextBlock { Text = labelText, FontSize = 9, FontFamily = new FontFamily("Consolas"), TextAlignment = TextAlignment.Right, Width = 24 };
                scaleCanvas.Children.Add(text);
                dbLabels.Add(text);
            }
            LayoutScaleLabels(188);

            var body = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            body.Children.Add(scaleCanvas);
            body.Children.Add(meterWell);
            SetRow(body, 1);
            Children.Add(body);

            dbPill.Child = dbReadout;
            SetRow(dbPill, 2);
            Children.Add(dbPill);

            ApplyTheme(true);
            Update(0, 0, false, false);
        }

        private void LayoutScaleLabels(double height)
        {
            double usable = Math.Max(40, height - 14);
            double[] positions = [0, 0.23, 0.46, 0.69, 1.0];
            for (int i = 0; i < dbLabels.Count; i++)
            {
                Canvas.SetLeft(dbLabels[i], 0);
                Canvas.SetTop(dbLabels[i], 2 + usable * positions[i]);
            }
        }

        internal void ApplyTheme(bool dark)
        {
            isDark = dark;
            meterWell.Background = new SolidColorBrush(Color.FromArgb(255, 0x0F, 0x12, 0x16));
            meterWell.BorderBrush = new SolidColorBrush(dark ? Color.FromArgb(255, 0x2B, 0x30, 0x3A) : Color.FromArgb(255, 0x9B, 0xA3, 0xB2));
            dbPill.Background = new SolidColorBrush(dark ? Color.FromArgb(255, 0x10, 0x13, 0x18) : Color.FromArgb(255, 0xE2, 0xE6, 0xED));
            dbPill.BorderBrush = new SolidColorBrush(dark ? Color.FromArgb(255, 0x26, 0x2C, 0x36) : Color.FromArgb(255, 0xB6, 0xBE, 0xCA));
            dbReadout.Foreground = new SolidColorBrush(dark ? Color.FromArgb(255, 0xE2, 0xE8, 0xF0) : Color.FromArgb(255, 0x1E, 0x29, 0x3B));

            var labelBrush = new SolidColorBrush(dark ? Color.FromArgb(255, 0x7A, 0x82, 0x94) : Color.FromArgb(255, 0x56, 0x60, 0x70));
            foreach (var label in dbLabels) label.Foreground = labelBrush;
            Update(lastLevel, lastPeak, lastMonitoring, lastCapturing);
        }

        internal void Update(double level, double peakHold, bool monitoringActive, bool capturing)
        {
            lastLevel = level = Math.Clamp(level, 0, 1);
            lastPeak = peakHold = Math.Clamp(peakHold, 0, 1);
            lastMonitoring = monitoringActive;
            lastCapturing = capturing;

            var primaryBrush = monitoringActive ? greenPrimaryBrush : redPrimaryBrush;
            var topBrush = monitoringActive ? greenTopBrush : redTopBrush;
            var deepBrush = monitoringActive ? greenDeepBrush : redDeepBrush;
            var peakBrush = monitoringActive ? greenPeakBrush : redPeakBrush;
            var unlitBrush = monitoringActive ? greenUnlitBrush : redUnlitBrush;

            stateDot.Fill = primaryBrush;
            stateLabel.Text = monitoringActive ? "耳返 · 出声" : capturing ? "试麦 · 静音" : "试麦 · 待机";
            stateLabel.Foreground = monitoringActive
                ? (isDark ? greenTopBrush : greenDeepBrush)
                : (isDark ? redTopBrush : redDeepBrush);

            dbReadout.Text = level <= 0.005 ? "-∞ dB" : $"{Math.Round(-52.0 * (1.0 - level)):0} dB";

            if (segmented)
            {
                int peakSegment = peakHold > 0.015 ? Math.Clamp((int)Math.Floor(peakHold * SegmentCount), 0, SegmentCount - 1) : -1;
                for (int i = 0; i < SegmentCount; i++)
                {
                    double threshold = (i + 0.45) / SegmentCount;
                    bool lit = level >= threshold;
                    bool isPeak = i == peakSegment;
                    segments[i].Background = lit
                        ? (i >= SegmentCount - 3 ? topBrush : i >= 5 ? primaryBrush : deepBrush)
                        : isPeak ? peakBrush : unlitBrush;
                }
            }
            else
            {
                double innerHeight = Math.Max(20, meterWell.Height - 12);
                smoothBar.Height = innerHeight * level;
                smoothBar.Background = monitoringActive ? greenSmoothBrush : redSmoothBrush;

                if (peakHold > 0.015)
                {
                    peakLine.Visibility = Visibility.Visible;
                    peakLine.Background = peakBrush;
                    peakLine.Margin = new Thickness(0, 0, 0, Math.Clamp(innerHeight * peakHold - 2, 0, innerHeight - 2));
                }
                else
                {
                    peakLine.Visibility = Visibility.Collapsed;
                }
            }
        }
    }
}
