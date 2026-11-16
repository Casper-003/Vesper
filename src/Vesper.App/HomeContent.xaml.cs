using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.System;
using Windows.UI;

namespace Vesper.App;

public sealed partial class HomeContent : UserControl
{
    private const double CompactWidthBreakpoint = 800;
    private const double CompactHeightBreakpoint = 220;

    private readonly MixerVolumeControl vocalControl;
    private readonly MixerVolumeControl bgmControl;
    private readonly MixerLevelMeter micMeter;
    private readonly DispatcherTimer meterTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly SolidColorBrush greenLedBrush = new(Color.FromArgb(255, 0x22, 0xC5, 0x5E));
    private readonly SolidColorBrush redLedBrush = new(Color.FromArgb(255, 0xEF, 0x44, 0x44));
    private readonly SolidColorBrush amberLedBrush = new(Color.FromArgb(255, 0xF5, 0x9E, 0x0B));

    private MonitoringPanel? controller;
    private CompositionRoundedRectangleGeometry? stageClipGeometry;
    private bool syncingFromController;
    private bool homeActive;
    private bool compactControls;
    private bool stagePointerInside;
    private bool stageButtonFocused;
    private bool stageLoading;
    private double smoothedLevel;
    private double peakHoldLevel;
    private int peakHoldTicks;

    internal event Action? StartSingingRequested;

    internal ListView QueueList => HomeQueueSongs;
    internal ContentControl CurrentQueueHost => HomeQueueCurrentContent;

    public HomeContent()
    {
        InitializeComponent();

        vocalControl = new MixerVolumeControl("MIC", "人声音量", Color.FromArgb(255, 0x39, 0xC5, 0xBB));
        bgmControl = new MixerVolumeControl("BGM", "伴奏音量", Color.FromArgb(255, 0x39, 0xC5, 0xBB));
        micMeter = new MixerLevelMeter();

        VocalVolumeHost.Children.Add(vocalControl);
        MicMeterHost.Children.Add(micMeter);
        BgmVolumeHost.Children.Add(bgmControl);

        vocalControl.ValueChanged += value =>
        {
            if (!syncingFromController && controller is not null) controller.VocalVolume = value;
        };
        bgmControl.ValueChanged += value =>
        {
            if (!syncingFromController && controller is not null) controller.BgmVolume = value;
        };

        ConfigureStageButtonVisuals();
        StageScreenClip.SizeChanged += OnStageScreenClipSizeChanged;
        StageEnterButton.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(160) };
        StageCaptionBar.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(180) };
        StageAmbientGlow.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(260) };

        meterTimer.Tick += OnMeterTick;
        Loaded += OnHomeLoaded;
        Unloaded += OnHomeUnloaded;
        ActualThemeChanged += (_, _) => ApplyVisualTheme();
        ApplyVisualTheme();
    }

    internal void ConfigureQueue(object itemsSource, DataTemplate itemTemplate, Style itemContainerStyle)
    {
        HomeQueueSongs.ItemsSource = itemsSource;
        HomeQueueSongs.ItemTemplate = itemTemplate;
        HomeQueueSongs.ItemContainerStyle = itemContainerStyle;
        HomeQueueCurrentContent.ContentTemplate = itemTemplate;
    }

    internal void UpdateQueueChrome(QueueSongItem? currentRow, string currentStateText, int pendingCount)
    {
        HomeQueueCurrentContent.Content = currentRow;
        HomeQueueCurrentArea.Visibility = currentRow is null ? Visibility.Collapsed : Visibility.Visible;
        HomeQueueCurrentState.Text = currentStateText;
        HomeQueueCount.Text = pendingCount.ToString();
        HomeQueueEmpty.Visibility = pendingCount == 0 ? Visibility.Visible : Visibility.Collapsed;
        HomeQueueEmptyTitle.Text = currentRow is null ? "暂无待播" : "暂无后续";
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

    internal void SetStagePresentation(ImageSource? image, bool isVideoFrame, string caption, uint? swatchColor,
        bool hasSong, bool isLoading, bool isPlayingSession)
    {
        stageLoading = isLoading;
        StagePlayGlyph.Visibility = isLoading ? Visibility.Collapsed : Visibility.Visible;
        StageLoadingRing.IsActive = isLoading;
        StageLoadingRing.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
        StageEnterButton.IsEnabled = !isLoading;

        string actionLabel = isPlayingSession ? "返回演唱" : "开始演唱";
        ToolTipService.SetToolTip(StageEnterButton, actionLabel);
        AutomationProperties.SetName(StageEnterButton, actionLabel);

        if (hasSong && image is not null)
        {
            StagePreviewImage.Source = image;
            StagePreviewImage.Stretch = isVideoFrame ? Stretch.UniformToFill : Stretch.Uniform;
            StagePreviewImage.Margin = isVideoFrame ? new Thickness(0) : new Thickness(20, 14, 20, 26);
            StagePreviewImage.Visibility = Visibility.Visible;
            StageLogoPlaceholder.Visibility = Visibility.Collapsed;
        }
        else
        {
            StagePreviewImage.Source = null;
            StagePreviewImage.Visibility = Visibility.Collapsed;
            StageLogoPlaceholder.Visibility = hasSong ? Visibility.Collapsed : Visibility.Visible;
        }

        StageCaptionText.Text = hasSong ? caption : "";
        StageCaptionBar.Opacity = hasSong && !string.IsNullOrWhiteSpace(caption) ? 1 : 0;

        ApplyStageAlbumColor(hasSong ? swatchColor : null, hasSong);
        RefreshStageEnterButtonVisibility();
    }

    internal Rect? GetStageScreenBounds(UIElement relativeTo)
    {
        if (!IsLoaded || StageScreenBorder.ActualWidth < 8 || StageScreenBorder.ActualHeight < 8) return null;
        return StageScreenBorder.TransformToVisual(relativeTo)
            .TransformBounds(new Rect(0, 0, StageScreenBorder.ActualWidth, StageScreenBorder.ActualHeight));
    }

    private void ApplyStageAlbumColor(uint? swatchColor, bool hasSong)
    {
        if (!hasSong || swatchColor is not { } argb)
        {
            StageAmbientGlow.Opacity = 0;
            StageColorWash.Opacity = 0;
            StageScreenBorder.Background = new SolidColorBrush(Color.FromArgb(255, 0x14, 0x16, 0x1B));
            StageScreenBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 0x2C, 0x30, 0x38));
            return;
        }

        byte r = (byte)(argb >> 16);
        byte g = (byte)(argb >> 8);
        byte b = (byte)argb;

        byte Blend(byte baseVal, byte target, double factor) =>
            (byte)Math.Clamp((int)Math.Round(baseVal + (target - baseVal) * factor), 0, 255);

        var topScreen = Color.FromArgb(255, Blend(0x14, r, 0.36), Blend(0x16, g, 0.36), Blend(0x1C, b, 0.36));
        var midScreen = Color.FromArgb(255, Blend(0x11, r, 0.28), Blend(0x13, g, 0.28), Blend(0x18, b, 0.28));
        var botScreen = Color.FromArgb(255, Blend(0x0D, r, 0.18), Blend(0x0F, g, 0.18), Blend(0x14, b, 0.18));
        var screenGrad = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        screenGrad.GradientStops.Add(new GradientStop { Color = topScreen, Offset = 0 });
        screenGrad.GradientStops.Add(new GradientStop { Color = midScreen, Offset = 0.55 });
        screenGrad.GradientStops.Add(new GradientStop { Color = botScreen, Offset = 1 });
        StageScreenBorder.Background = screenGrad;
        StageScreenBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(165, r, g, b));

        var washBrush = new RadialGradientBrush
        {
            MappingMode = BrushMappingMode.RelativeToBoundingBox,
            Center = new Point(0.5, 0.45),
            GradientOrigin = new Point(0.5, 0.45),
            RadiusX = 0.80,
            RadiusY = 0.80
        };
        washBrush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(150, r, g, b), Offset = 0 });
        washBrush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(52, r, g, b), Offset = 0.65 });
        washBrush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0, r, g, b), Offset = 1 });
        StageColorWash.Background = washBrush;
        StageColorWash.Opacity = 1;

        var ambientBrush = new RadialGradientBrush
        {
            MappingMode = BrushMappingMode.RelativeToBoundingBox,
            Center = new Point(0.5, 0.5),
            GradientOrigin = new Point(0.5, 0.5),
            RadiusX = 0.56,
            RadiusY = 0.56
        };
        ambientBrush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(145, r, g, b), Offset = 0 });
        ambientBrush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(58, r, g, b), Offset = 0.55 });
        ambientBrush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0, r, g, b), Offset = 1 });
        StageAmbientGlow.Background = ambientBrush;
        StageAmbientGlow.Opacity = 1;
    }

    private void ConfigureStageButtonVisuals()
    {
        StageEnterButton.Background = SingingView.CreateGlassMaterial();
        foreach (string state in new[] { "ButtonBackground", "ButtonBackgroundPointerOver", "ButtonBackgroundPressed", "ButtonBackgroundDisabled" })
            StageEnterButton.Resources[state] = SingingView.CreateGlassMaterial();
        foreach (string state in new[] { "ButtonBorderBrush", "ButtonBorderBrushPointerOver", "ButtonBorderBrushPressed", "ButtonBorderBrushDisabled" })
            StageEnterButton.Resources[state] = new SolidColorBrush(Colors.Transparent);

        StageEnterButton.PointerEntered += (_, _) => AnimateStageButtonScale(1.06f);
        StageEnterButton.PointerExited += (_, _) => AnimateStageButtonScale(1f);
        StageEnterButton.PointerPressed += (_, _) => AnimateStageButtonScale(.94f);
        StageEnterButton.PointerReleased += (_, _) => AnimateStageButtonScale(1.06f);
        StageEnterButton.PointerCanceled += (_, _) => AnimateStageButtonScale(1f);
        StageEnterButton.PointerCaptureLost += (_, _) => AnimateStageButtonScale(1f);
    }

    private void AnimateStageButtonScale(float scale)
    {
        StageEnterButton.CenterPoint = new Vector3(31, 31, 0);
        var compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
        var animation = compositor.CreateVector3KeyFrameAnimation();
        animation.Target = "Scale";
        animation.InsertExpressionKeyFrame(0, "this.StartingValue");
        animation.InsertKeyFrame(1, new Vector3(scale));
        animation.Duration = TimeSpan.FromMilliseconds(120);
        StageEnterButton.StartAnimation(animation);
    }

    private void OnStageScreenClipSizeChanged(object sender, SizeChangedEventArgs args)
    {
        var visual = ElementCompositionPreview.GetElementVisual(StageScreenClip);
        if (stageClipGeometry is null)
        {
            stageClipGeometry = visual.Compositor.CreateRoundedRectangleGeometry();
            stageClipGeometry.CornerRadius = new Vector2(11);
            visual.Clip = visual.Compositor.CreateGeometricClip(stageClipGeometry);
        }
        stageClipGeometry.Size = new Vector2((float)Math.Max(0, args.NewSize.Width), (float)Math.Max(0, args.NewSize.Height));
    }

    private void OnStagePointerEntered(object sender, PointerRoutedEventArgs e)
    {
        stagePointerInside = true;
        RefreshStageEnterButtonVisibility();
    }

    private void OnStagePointerExited(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(StageScreenBorder).Position;
        if (point.X >= 0 && point.Y >= 0 && point.X <= StageScreenBorder.ActualWidth && point.Y <= StageScreenBorder.ActualHeight) return;
        stagePointerInside = false;
        RefreshStageEnterButtonVisibility();
    }

    private void OnStageButtonFocusChanged(object sender, RoutedEventArgs e)
    {
        stageButtonFocused = StageEnterButton.FocusState != FocusState.Unfocused;
        RefreshStageEnterButtonVisibility();
    }

    private void RefreshStageEnterButtonVisibility()
    {
        bool show = stagePointerInside || stageButtonFocused || stageLoading;
        StageEnterButton.Opacity = show ? 1 : 0;
        StageEnterButton.IsHitTestVisible = show;
    }

    private void OnStageEnterClicked(object sender, RoutedEventArgs e) => StartSingingRequested?.Invoke();

    private void OnHomeLoaded(object sender, RoutedEventArgs e)
    {
        ApplyVisualTheme();
        UpdateResponsiveLayout();
        SyncFromController();
        SyncMeterTimer();
    }

    private void OnHomeUnloaded(object sender, RoutedEventArgs e)
    {
        stagePointerInside = stageButtonFocused = false;
        RefreshStageEnterButtonVisibility();
        meterTimer.Stop();
    }

    private void OnConsoleSizeChanged(object sender, SizeChangedEventArgs e) => UpdateResponsiveLayout();
    private void OnLeftWellSizeChanged(object sender, SizeChangedEventArgs e) => UpdateResponsiveLayout();

    private void OnCenterStageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        double availW = e.NewSize.Width;
        double availH = e.NewSize.Height;
        if (availW <= 0 || availH <= 0) return;

        // 留出明显的呼吸外边距，大屏幕不沾满整个中间区域
        double maxW = Math.Max(160, Math.Min(availW - 44, availW * 0.88));
        double maxH = Math.Max(90, Math.Min(availH - 36, availH * 0.86));
        double targetW = Math.Min(maxW, maxH * 16.0 / 9.0);
        double targetH = targetW * 9.0 / 16.0;

        StageScreenBorder.Width = targetW;
        StageScreenBorder.Height = targetH;
        StageAmbientGlow.Width = targetW + 68;
        StageAmbientGlow.Height = targetH + 56;
    }

    private void UpdateResponsiveLayout()
    {
        double rootWidth = ConsoleRoot.ActualWidth;
        double wellHeight = LeftFaderWell.ActualHeight;
        if (rootWidth <= 0) return;

        bool narrow = rootWidth < CompactWidthBreakpoint;
        LeftColumnDef.Width = new GridLength(narrow ? 168 : 228);
        RightColumnDef.Width = new GridLength(narrow ? 216 : 260);
        ConsoleRoot.Padding = narrow ? new Thickness(12) : new Thickness(18);
        ConsoleRoot.ColumnSpacing = narrow ? 10 : 14;

        bool useKnobs = narrow || (wellHeight > 0 && wellHeight < CompactHeightBreakpoint);
        if (useKnobs != compactControls)
        {
            compactControls = useKnobs;
            vocalControl.CompactMode = useKnobs;
            bgmControl.CompactMode = useKnobs;
            micMeter.CompactMode = useKnobs;

            if (useKnobs)
            {
                LeftFaderGrid.ColumnDefinitions[2].Width = new GridLength(0);
                Grid.SetRow(VocalVolumeHost, 0); Grid.SetColumn(VocalVolumeHost, 0); Grid.SetRowSpan(VocalVolumeHost, 1);
                Grid.SetRow(BgmVolumeHost, 1); Grid.SetColumn(BgmVolumeHost, 0); Grid.SetRowSpan(BgmVolumeHost, 1);
                Grid.SetRow(MicMeterHost, 0); Grid.SetColumn(MicMeterHost, 1); Grid.SetRowSpan(MicMeterHost, 2);
            }
            else
            {
                LeftFaderGrid.ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star);
                Grid.SetRow(VocalVolumeHost, 0); Grid.SetColumn(VocalVolumeHost, 0); Grid.SetRowSpan(VocalVolumeHost, 1);
                Grid.SetRow(MicMeterHost, 0); Grid.SetColumn(MicMeterHost, 1); Grid.SetRowSpan(MicMeterHost, 1);
                Grid.SetRow(BgmVolumeHost, 0); Grid.SetColumn(BgmVolumeHost, 2); Grid.SetRowSpan(BgmVolumeHost, 1);
            }
        }

        if (wellHeight > 0)
        {
            double targetHeight = Math.Max(140, Math.Min(300, wellHeight - 68));
            vocalControl.SetTrackHeight(targetHeight);
            bgmControl.SetTrackHeight(targetHeight);
            micMeter.SetMeterHeight(useKnobs ? Math.Clamp(wellHeight - 54, 130, 220) : targetHeight);
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

            MonitoringButtonLed.Fill = monitoringActive ? greenLedBrush : redLedBrush;
            MonitoringToggleLabel.Text = controller.IsStarting ? "开启中" : "耳返";

            UpdateStatusReadout(monitoringActive, exclusive);
            UpdateModeButtons(exclusive);
            UpdateMonitoringButtonStyle(monitoringActive);
            micMeter.Update(smoothedLevel, peakHoldLevel, monitoringActive, controller.IsMeterCapturing);
        }
        finally
        {
            syncingFromController = false;
        }
    }

    private void UpdateStatusReadout(bool monitoringActive, bool exclusive)
    {
        if (controller is null) return;
        bool capturing = controller.IsMeterCapturing;

        if (monitoringActive)
        {
            ParamStatusLed.Fill = greenLedBrush;
            if (controller.RunningInfo is { } info)
            {
                double inMs = info.InputPeriod * 1000d / info.InputRate;
                double outMs = info.OutputPeriod * 1000d / info.OutputRate;
                ParamStatusText.Text = $"耳返 · {inMs:F1}/{outMs:F1}ms";
            }
            else
            {
                ParamStatusText.Text = "耳返中";
            }
        }
        else if (controller.IsEnumerating || controller.IsStarting)
        {
            ParamStatusLed.Fill = amberLedBrush;
            ParamStatusText.Text = controller.IsEnumerating ? "扫描设备…" : "准备中…";
        }
        else if (capturing)
        {
            ParamStatusLed.Fill = redLedBrush;
            ParamStatusText.Text = exclusive ? "试麦 · 独占模式" : "试麦 · 静音";
        }
        else
        {
            ParamStatusLed.Fill = controller.SelectedInput is null ? amberLedBrush : redLedBrush;
            ParamStatusText.Text = controller.StatusText;
        }
        ToolTipService.SetToolTip(ParamStatusText, controller.StatusText);
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

    // 响应式物理推子（宽/高窗口）与旋转旋钮（紧凑窗口），固定槽位宽度避免数值变化引起移位
    private sealed class MixerVolumeControl : Grid
    {
        private const double FaderTopOffset = 10;

        private readonly Color accentColor;
        private readonly TextBlock titleBlock = new() { FontSize = 11, FontFamily = new FontFamily("Consolas"), FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
        private readonly TextBlock valueReadout = new() { FontSize = 11, FontFamily = new FontFamily("Consolas"), FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center };
        private readonly Border readoutPill = new() { Width = 48, CornerRadius = new CornerRadius(5), Padding = new Thickness(0, 3, 0, 3), HorizontalAlignment = HorizontalAlignment.Center, BorderThickness = new Thickness(1) };

        // Fader parts
        private readonly Grid faderHost = new() { Width = 62, Height = 192 };
        private readonly Canvas scaleCanvas = new() { Width = 24, Height = 188 };
        private readonly Canvas faderTrackCanvas = new() { Width = 38, Height = 188, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Background = new SolidColorBrush(Colors.Transparent) };
        private readonly Border faderGroove = new() { Width = 6, Height = 164, CornerRadius = new CornerRadius(3) };
        private readonly Border faderFill = new() { Width = 6, CornerRadius = new CornerRadius(3) };
        private readonly Border faderCap = new() { Width = 28, Height = 18, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1) };
        private readonly Rectangle faderCapLine = new() { Width = 18, Height = 2, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        private readonly List<(Rectangle Tick, int Step, bool LeftSide)> tickMarks = [];

        // Knob parts
        private readonly Grid knobHost = new() { Width = 76, Height = 76, Visibility = Visibility.Collapsed, Background = new SolidColorBrush(Colors.Transparent) };
        private readonly Microsoft.UI.Xaml.Shapes.Path knobTrackArc = new() { StrokeThickness = 3.5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        private readonly Microsoft.UI.Xaml.Shapes.Path knobValueArc = new() { StrokeThickness = 3.5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        private readonly ArcSegment knobValueSegment = new() { Size = new Size(30, 30), SweepDirection = SweepDirection.Clockwise };
        private readonly Ellipse knobOuter = new() { Width = 44, Height = 44, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, StrokeThickness = 1.5 };
        private readonly Ellipse knobInner = new() { Width = 36, Height = 36, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        private readonly Grid knobRotor = new() { Width = 44, Height = 44, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        private readonly RotateTransform knobRotate = new() { CenterX = 22, CenterY = 22 };
        private readonly Rectangle knobPointer = new() { Width = 2.5, Height = 9, RadiusX = 1.25, RadiusY = 1.25, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 5, 0, 0) };

        private double faderTravel = 164;
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
            this.accentColor = accentColor;
            IsTabStop = true;
            UseSystemFocusVisuals = true;
            AutomationProperties.SetName(this, accessibleName);

            RowSpacing = 6;
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            titleBlock.Text = shortLabel;
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
            SetTrackHeight(188);
        }

        private void BuildFader()
        {
            faderHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            faderHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });

            for (int step = 0; step <= 10; step++)
            {
                bool major = step % 5 == 0 || step == 2 || step == 8;
                var leftTick = new Rectangle { Width = major ? 8 : 4, Height = 1 };
                Canvas.SetLeft(leftTick, 24 - leftTick.Width - 2);
                scaleCanvas.Children.Add(leftTick);
                tickMarks.Add((leftTick, step, true));

                var rightTick = new Rectangle { Width = step % 5 == 0 ? 6 : 3, Height = 1 };
                Canvas.SetLeft(rightTick, 26);
                faderTrackCanvas.Children.Add(rightTick);
                tickMarks.Add((rightTick, step, false));
            }
            faderHost.Children.Add(scaleCanvas);

            Canvas.SetLeft(faderGroove, 16);
            Canvas.SetTop(faderGroove, FaderTopOffset);
            faderTrackCanvas.Children.Add(faderGroove);

            faderFill.Background = new SolidColorBrush(accentColor);
            Canvas.SetLeft(faderFill, 16);
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
            const double center = 38;
            const double radius = 30;

            var tickCanvas = new Canvas { Width = 76, Height = 76, IsHitTestVisible = false };
            for (int i = 0; i <= 10; i++)
            {
                double deg = 135.0 + 27.0 * i;
                double rad = deg * Math.PI / 180.0;
                double rOuter = 36.5;
                double rInner = i % 5 == 0 ? 32 : 33.8;
                var line = new Line
                {
                    X1 = center + rInner * Math.Cos(rad),
                    Y1 = center + rInner * Math.Sin(rad),
                    X2 = center + rOuter * Math.Cos(rad),
                    Y2 = center + rOuter * Math.Sin(rad),
                    StrokeThickness = i % 5 == 0 ? 1.4 : 1
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

        internal void SetTrackHeight(double height)
        {
            double travel = Math.Clamp(height - 22, 110, 270);
            if (Math.Abs(faderTravel - travel) < 0.5) return;
            faderTravel = travel;
            faderHost.Height = travel + 22;
            scaleCanvas.Height = travel + 20;
            faderTrackCanvas.Height = travel + 20;
            faderGroove.Height = travel;
            foreach (var (tick, step, _) in tickMarks)
                Canvas.SetTop(tick, FaderTopOffset + faderTravel * (1.0 - step / 10.0));
            UpdateVisual();
        }

        internal void ApplyTheme(bool dark)
        {
            titleBlock.Foreground = new SolidColorBrush(dark ? Color.FromArgb(255, 0x8E, 0x96, 0xA4) : Color.FromArgb(255, 0x56, 0x60, 0x70));
            readoutPill.Background = new SolidColorBrush(dark ? Color.FromArgb(255, 0x10, 0x13, 0x18) : Color.FromArgb(255, 0xE2, 0xE6, 0xED));
            readoutPill.BorderBrush = new SolidColorBrush(dark ? Color.FromArgb(255, 0x26, 0x2C, 0x36) : Color.FromArgb(255, 0xB6, 0xBE, 0xCA));
            valueReadout.Foreground = new SolidColorBrush(dark ? Color.FromArgb(255, 0xE2, 0xE8, 0xF0) : Color.FromArgb(255, 0x1E, 0x29, 0x3B));

            faderGroove.Background = new SolidColorBrush(dark ? Color.FromArgb(255, 0x0D, 0x0F, 0x13) : Color.FromArgb(255, 0xB8, 0xBF, 0xCB));
            faderCap.Background = new SolidColorBrush(dark ? Color.FromArgb(255, 0x38, 0x3C, 0x46) : Color.FromArgb(255, 0xFB, 0xFC, 0xFD));
            faderCap.BorderBrush = new SolidColorBrush(dark ? Color.FromArgb(255, 0x60, 0x67, 0x76) : Color.FromArgb(255, 0x8F, 0x98, 0xA8));
            faderCapLine.Fill = new SolidColorBrush(dark ? Color.FromArgb(255, 0xE2, 0xE8, 0xF0) : Color.FromArgb(255, 0x2B, 0x30, 0x3A));

            var tickBrush = new SolidColorBrush(dark ? Color.FromArgb(255, 0x54, 0x5B, 0x6B) : Color.FromArgb(255, 0x8A, 0x93, 0xA2));
            foreach (var (tick, _, _) in tickMarks) tick.Fill = tickBrush;

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
                double fillHeight = faderTravel * t;
                faderFill.Height = fillHeight;
                Canvas.SetTop(faderFill, FaderTopOffset + faderTravel - fillHeight);
                double capCenterY = FaderTopOffset + faderTravel * (1.0 - t);
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
                    knobValueSegment.Point = new Point(38 + 30 * Math.Cos(endRad), 38 + 30 * Math.Sin(endRad));
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
            double ratio = 1.0 - (y - FaderTopOffset) / faderTravel;
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

    // 实时麦克风电平灯柱：未开耳返红色／开启耳返绿色，支持分段 LED 与连续平滑光条
    private sealed class MixerLevelMeter : Grid
    {
        private const int SegmentCount = 16;

        private readonly TextBlock titleBlock = new() { Text = "电平", FontSize = 11, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
        private readonly Border meterWell = new() { Width = 34, Height = 188, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Padding = new Thickness(4) };
        private readonly Grid segmentedGrid = new() { RowSpacing = 2 };
        private readonly Border[] segments = new Border[SegmentCount];
        private readonly Grid smoothHost = new() { Visibility = Visibility.Collapsed };
        private readonly Border smoothBar = new() { VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Stretch, CornerRadius = new CornerRadius(3), Height = 0 };
        private readonly Border peakLine = new() { VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Stretch, Height = 2, CornerRadius = new CornerRadius(1), Visibility = Visibility.Collapsed };
        private readonly Border dbPill = new() { Width = 48, CornerRadius = new CornerRadius(5), Padding = new Thickness(0, 3, 0, 3), HorizontalAlignment = HorizontalAlignment.Center, BorderThickness = new Thickness(1) };
        private readonly TextBlock dbReadout = new() { Text = "-∞", FontSize = 11, FontFamily = new FontFamily("Consolas"), FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center };

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
        private double lastLevel, lastPeak;
        private bool lastMonitoring;

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
                Update(lastLevel, lastPeak, lastMonitoring, true);
            }
        }

        internal bool CompactMode { get; set; }

        internal MixerLevelMeter()
        {
            RowSpacing = 6;
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            AutomationProperties.SetName(this, "实时麦克风电平表");

            Children.Add(titleBlock);

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

            SetRow(meterWell, 1);
            Children.Add(meterWell);

            dbPill.Child = dbReadout;
            SetRow(dbPill, 2);
            Children.Add(dbPill);

            ApplyTheme(true);
            Update(0, 0, false, false);
        }

        internal void SetMeterHeight(double height)
        {
            double targetHeight = Math.Clamp(height, 120, 290);
            if (Math.Abs(meterWell.Height - targetHeight) < 0.5) return;
            meterWell.Height = targetHeight;
            Update(lastLevel, lastPeak, lastMonitoring, true);
        }

        internal void ApplyTheme(bool dark)
        {
            titleBlock.Foreground = new SolidColorBrush(dark ? Color.FromArgb(255, 0x8E, 0x96, 0xA4) : Color.FromArgb(255, 0x56, 0x60, 0x70));
            meterWell.Background = new SolidColorBrush(Color.FromArgb(255, 0x0F, 0x12, 0x16));
            meterWell.BorderBrush = new SolidColorBrush(dark ? Color.FromArgb(255, 0x2B, 0x30, 0x3A) : Color.FromArgb(255, 0x9B, 0xA3, 0xB2));
            dbPill.Background = new SolidColorBrush(dark ? Color.FromArgb(255, 0x10, 0x13, 0x18) : Color.FromArgb(255, 0xE2, 0xE6, 0xED));
            dbPill.BorderBrush = new SolidColorBrush(dark ? Color.FromArgb(255, 0x26, 0x2C, 0x36) : Color.FromArgb(255, 0xB6, 0xBE, 0xCA));
            dbReadout.Foreground = new SolidColorBrush(dark ? Color.FromArgb(255, 0xE2, 0xE8, 0xF0) : Color.FromArgb(255, 0x1E, 0x29, 0x3B));
            Update(lastLevel, lastPeak, lastMonitoring, true);
        }

        internal void Update(double level, double peakHold, bool monitoringActive, bool capturing)
        {
            lastLevel = level = Math.Clamp(level, 0, 1);
            lastPeak = peakHold = Math.Clamp(peakHold, 0, 1);
            lastMonitoring = monitoringActive;

            var primaryBrush = monitoringActive ? greenPrimaryBrush : redPrimaryBrush;
            var topBrush = monitoringActive ? greenTopBrush : redTopBrush;
            var deepBrush = monitoringActive ? greenDeepBrush : redDeepBrush;
            var peakBrush = monitoringActive ? greenPeakBrush : redPeakBrush;
            var unlitBrush = monitoringActive ? greenUnlitBrush : redUnlitBrush;

            dbReadout.Text = level <= 0.005 ? "-∞" : $"{Math.Round(-52.0 * (1.0 - level)):0}dB";

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
                double innerHeight = Math.Max(20, meterWell.Height - 10);
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
