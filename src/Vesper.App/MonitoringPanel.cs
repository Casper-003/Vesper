using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Vesper.App;

internal sealed class MonitoringPanel : UserControl, IDisposable
{
    private readonly ComboBox input = new() { Header = "麦克风", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox output = new() { Header = "人声输出设备", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox mode = new() { Header = "音频模式", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Slider volume = new() { Header = "返听音量", Minimum = 0, Maximum = 100, Value = 20 };
    private readonly Slider bgmVolume = new() { Header = "伴奏音量", Minimum = 0, Maximum = 100, Value = 60 };
    private readonly ComboBox meterStyle = new() { Header = "麦克风电平表样式", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button toggle = new() { Content = "开启实时返听", IsEnabled = false };
    private readonly Button refresh = new() { Content = "刷新设备" };
    private readonly TextBlock status = new() { Text = "返听已关闭", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock usage = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Func<bool> mediaActive;
    private readonly DispatcherTimer diagnostics = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private IReadOnlyList<MonitoringDevice> inputDevices = [];
    private IReadOnlyList<MonitoringDevice> outputDevices = [];
    private MonitoringInfo? runningInfo;
    private WasapiMonitoring? engine;
    private WasapiMicMeter? probe;
    private Task stopping = Task.CompletedTask;
    private Task probeStopping = Task.CompletedTask;
    private Task? deviceLoadTask;
    private long revision, probeRevision;
    private bool disposed, enumerating, loaded, starting, homePageActive, suppressNotifications;

    internal event Action? StateChanged;

    internal MonitoringPanel(ShellPreferences preferences, Func<bool> mediaActive)
    {
        this.mediaActive = mediaActive;
        var layout = new StackPanel { Spacing = 12, MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Left };
        Content = layout;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        layout.Children.Add(new TextBlock { Text = "实时人声返听", FontSize = 20, Margin = new Thickness(0, 20, 0, 0) });
        layout.Children.Add(new TextBlock { Text = "将麦克风人声实时送到耳机或音箱。使用音箱时请从较低音量开始，避免麦克风与音箱形成啸叫。", TextWrapping = TextWrapping.Wrap });
        mode.Items.Add(new ComboBoxItem { Content = "共享低延迟" });
        mode.Items.Add(new ComboBoxItem { Content = "独占模式" });
        mode.SelectedIndex = 0;
        meterStyle.Items.Add(new ComboBoxItem { Content = "分段式 LED 格栅" });
        meterStyle.Items.Add(new ComboBoxItem { Content = "连续平滑光条" });
        meterStyle.SelectedIndex = preferences.SegmentedLevelMeter ? 0 : 1;
        volume.Value = Math.Clamp(double.IsFinite(preferences.VocalVolume) ? preferences.VocalVolume : 20, 0, 100);
        bgmVolume.Value = Math.Clamp(double.IsFinite(preferences.BgmVolume) ? preferences.BgmVolume : 60, 0, 100);

        foreach (UIElement element in new UIElement[] { input, output, mode, usage, volume, bgmVolume, meterStyle, toggle, refresh, status })
            layout.Children.Add(element);

        AutomationProperties.SetName(input, "返听麦克风");
        AutomationProperties.SetName(output, "人声输出设备");
        AutomationProperties.SetName(volume, "返听音量");
        AutomationProperties.SetName(bgmVolume, "伴奏音量");
        AutomationProperties.SetName(meterStyle, "麦克风电平表样式");

        toggle.Click += async (_, _) => await ToggleMonitoringAsync();
        refresh.Click += async (_, _) => await RefreshDevicesAsync();
        input.SelectionChanged += (_, _) => { if (!suppressNotifications) ConfigurationChanged(); };
        output.SelectionChanged += (_, _) => { if (!suppressNotifications) ConfigurationChanged(); };
        mode.SelectionChanged += (_, _) => { if (!suppressNotifications) ConfigurationChanged(); };
        volume.ValueChanged += (_, _) =>
        {
            if (engine is { } current) current.Gain = (float)(volume.Value / 100);
            if (!suppressNotifications) NotifyStateChanged();
        };
        bgmVolume.ValueChanged += (_, _) => { if (!suppressNotifications) NotifyStateChanged(); };
        meterStyle.SelectionChanged += (_, _) => { if (!suppressNotifications) NotifyStateChanged(); };
        Loaded += async (_, _) => await EnsureDevicesLoadedAsync();
        UpdateUsage();
        diagnostics.Tick += (_, _) =>
        {
            if (engine is not { } current || runningInfo is null) return;
            status.Text = runningInfo + $" · 采集中断 {current.CaptureDiscontinuities}／输出缺帧 {current.OutputShortages}";
            NotifyStateChanged();
        };
    }

    internal IReadOnlyList<MonitoringDevice> InputDevices => inputDevices;
    internal IReadOnlyList<MonitoringDevice> OutputDevices => outputDevices;

    internal MonitoringDevice? SelectedInput
    {
        get => input.SelectedItem as MonitoringDevice;
        set
        {
            if (value is null || (input.SelectedItem as MonitoringDevice)?.Id == value.Id) return;
            var match = inputDevices.FirstOrDefault(d => d.Id == value.Id);
            if (match is not null) input.SelectedItem = match;
        }
    }

    internal MonitoringDevice? SelectedOutput
    {
        get => output.SelectedItem as MonitoringDevice;
        set
        {
            if (value is null || (output.SelectedItem as MonitoringDevice)?.Id == value.Id) return;
            var match = outputDevices.FirstOrDefault(d => d.Id == value.Id);
            if (match is not null) output.SelectedItem = match;
        }
    }

    internal MonitoringMode SelectedMode
    {
        get => mode.SelectedIndex == 1 ? MonitoringMode.Exclusive : MonitoringMode.LowLatency;
        set
        {
            int index = value == MonitoringMode.Exclusive ? 1 : 0;
            if (mode.SelectedIndex != index) mode.SelectedIndex = index;
        }
    }

    internal double VocalVolume
    {
        get => volume.Value;
        set
        {
            double clamped = Math.Clamp(value, 0, 100);
            if (Math.Abs(volume.Value - clamped) > 0.05) volume.Value = clamped;
        }
    }

    internal double BgmVolume
    {
        get => bgmVolume.Value;
        set
        {
            double clamped = Math.Clamp(value, 0, 100);
            if (Math.Abs(bgmVolume.Value - clamped) > 0.05) bgmVolume.Value = clamped;
        }
    }

    internal bool SegmentedLevelMeter
    {
        get => meterStyle.SelectedIndex == 0;
        set
        {
            int index = value ? 0 : 1;
            if (meterStyle.SelectedIndex != index) meterStyle.SelectedIndex = index;
        }
    }

    internal bool IsMonitoringActive => engine is not null;
    internal bool IsEnumerating => enumerating;
    internal bool IsStarting => starting;
    internal bool CanToggleMonitoring => !enumerating && !starting && input.SelectedItem is MonitoringDevice && output.SelectedItem is MonitoringDevice;
    internal bool CanRefreshDevices => !enumerating && !starting && engine is null;
    internal string StatusText => status.Text;
    internal string UsageText => usage.Text;
    internal MonitoringInfo? RunningInfo => runningInfo;
    internal bool IsMeterCapturing => engine is not null || probe is not null;

    internal double ReadNormalizedLevel()
    {
        if (engine is { } activeEngine) return WasapiMonitoring.NormalizeMeterLevel(activeEngine.InputLevel);
        if (probe is { } activeProbe) return WasapiMonitoring.NormalizeMeterLevel(activeProbe.InputLevel);
        return 0;
    }

    internal Task EnsureDevicesLoadedAsync()
    {
        if (disposed) return Task.CompletedTask;
        if (deviceLoadTask is { IsCompleted: false } pending) return pending;
        if (loaded && (inputDevices.Count > 0 || outputDevices.Count > 0)) return Task.CompletedTask;
        loaded = true;
        deviceLoadTask = RefreshDevicesAsync();
        return deviceLoadTask;
    }

    internal void SetHomePageActive(bool active)
    {
        if (disposed) return;
        homePageActive = active;
        SyncProbe();
    }

    internal Task ToggleMonitoringAsync() => engine is null ? StartAsync() : StopAndNotifyAsync();

    private Task StopAndNotifyAsync()
    {
        Stop();
        NotifyStateChanged();
        return Task.CompletedTask;
    }

    private void ConfigurationChanged()
    {
        if (engine is not null) Stop();
        else revision++; // Also invalidate a start waiting for the prior worker to stop.
        UpdateUsage();
        toggle.IsEnabled = CanToggleMonitoring;
        refresh.IsEnabled = CanRefreshDevices;
        SyncProbe();
        NotifyStateChanged();
    }

    internal async Task RefreshDevicesAsync()
    {
        if (disposed || enumerating || engine is not null || starting) return;
        enumerating = true;
        refresh.IsEnabled = toggle.IsEnabled = false;
        StopProbe();
        NotifyStateChanged();
        try
        {
            await probeStopping;
            if (disposed) return;
            string? selectedInput = (input.SelectedItem as MonitoringDevice)?.Id;
            string? selectedOutput = (output.SelectedItem as MonitoringDevice)?.Id;
            var devices = await Task.Run(() => (Inputs: WasapiMonitoring.Devices(true), Outputs: WasapiMonitoring.Devices(false)));
            if (disposed) return;
            suppressNotifications = true;
            try
            {
                inputDevices = devices.Inputs;
                outputDevices = devices.Outputs;
                input.ItemsSource = inputDevices;
                output.ItemsSource = outputDevices;
                input.SelectedItem = inputDevices.FirstOrDefault(d => d.Id == selectedInput) ?? inputDevices.FirstOrDefault();
                output.SelectedItem = outputDevices.FirstOrDefault(d => d.Id == selectedOutput) ?? outputDevices.FirstOrDefault();
            }
            finally { suppressNotifications = false; }
            status.Text = inputDevices.Count == 0 ? "没有可用的麦克风，请连接后刷新。"
                : outputDevices.Count == 0 ? "没有可用的输出设备，请连接后刷新。" : "返听已关闭，请确认麦克风和人声输出设备后开启。";
        }
        catch (Exception)
        {
            if (!disposed) status.Text = "无法读取音频设备，请检查 Windows 音频服务后重试。";
        }
        finally
        {
            enumerating = false;
            if (!disposed)
            {
                loaded = true;
                ConfigurationChanged();
            }
        }
    }

    private async Task StartAsync()
    {
        if (disposed || starting || input.SelectedItem is not MonitoringDevice source || output.SelectedItem is not MonitoringDevice destination) return;
        long request = ++revision;
        MonitoringMode selectedMode = SelectedMode;
        if (selectedMode == MonitoringMode.Exclusive && mediaActive())
        {
            status.Text = "请先停止歌曲预览并退出当前演唱，再开启独占返听。";
            NotifyStateChanged();
            return;
        }
        starting = true;
        toggle.IsEnabled = false;
        refresh.IsEnabled = false;
        status.Text = "正在准备实时返听…";
        StopProbe();
        NotifyStateChanged();
        try
        {
            await Task.WhenAll(stopping, probeStopping);
            if (request != revision || disposed) return;
            WasapiMonitoring? current = null;
            current = new(source.Id, destination.Id, selectedMode,
                error => DispatcherQueue.TryEnqueue(() =>
                {
                    if (disposed || !ReferenceEquals(engine, current)) return;
                    Stop();
                    status.Text = Describe(error);
                    NotifyStateChanged();
                }));
            engine = current;
            current.Gain = (float)(volume.Value / 100);
            toggle.Content = "关闭实时返听";
            toggle.IsEnabled = true;
            NotifyStateChanged();
            current.Start();
            MonitoringInfo info = await current.Started;
            if (request == revision && !disposed)
            {
                runningInfo = info;
                status.Text = info.ToString();
                diagnostics.Start();
                NotifyStateChanged();
            }
        }
        catch (Exception error)
        {
            if (request != revision || disposed) return;
            Stop();
            status.Text = Describe(error);
            NotifyStateChanged();
        }
        finally
        {
            starting = false;
            if (request == revision && !disposed)
            {
                toggle.IsEnabled = CanToggleMonitoring;
                refresh.IsEnabled = CanRefreshDevices;
                if (engine is null) SyncProbe();
                NotifyStateChanged();
            }
        }
    }

    private static string Describe(Exception error) => error is NotSupportedException ? error.Message
        : error.HResult == unchecked((int)0x80070005) ? "无法访问麦克风。请在 Windows 隐私设置中允许桌面应用使用麦克风。"
        : error.HResult == unchecked((int)0x8889000A) ? "设备正被占用。请关闭使用该设备的播放／录音程序，或改用共享低延迟。"
        : $"返听已停止（{error.HResult:X8}）。请检查设备连接与独占权限，或更换模式后重试。";

    private void UpdateUsage() => usage.Text = mode.SelectedIndex == 1
        ? "独占会占用所选麦克风和输出设备，其他程序可能无法使用。请先停止其他播放／录音，再开启；当前用于干声返听对照，开始预览或演唱会关闭独占返听。设备不支持时会报错，不会自动切换模式。"
        : "共享低延迟可同时播放伴奏；人声到达即送出，不等待多包缓冲。";

    internal async Task ReleaseForPlaybackAsync()
    {
        if (engine is null || mode.SelectedIndex != 1) return;
        Stop();
        await stopping;
        if (!disposed)
        {
            status.Text = "为播放歌曲，独占返听已关闭；同时使用伴奏和返听请选择共享低延迟。";
            NotifyStateChanged();
        }
    }

    private void Stop()
    {
        revision++;
        diagnostics.Stop();
        runningInfo = null;
        var current = engine;
        engine = null;
        if (current is not null)
        {
            current.Dispose();
            stopping = current.Finished;
        }
        toggle.Content = "开启实时返听";
        status.Text = "返听已关闭";
        toggle.IsEnabled = CanToggleMonitoring;
        refresh.IsEnabled = CanRefreshDevices;
        if (!disposed && homePageActive && !starting && !enumerating) _ = ResumeProbeAfterStopAsync(stopping);
    }

    private async Task ResumeProbeAfterStopAsync(Task priorStopping)
    {
        long currentRev = probeRevision;
        try { await priorStopping; }
        catch { /* Ignored during stop cleanup. */ }
        if (!disposed && currentRev == probeRevision) SyncProbe();
    }

    private void SyncProbe()
    {
        long request = ++probeRevision;
        StopProbe();
        if (disposed || !homePageActive || engine is not null || starting || enumerating) return;
        if (SelectedInput is not { } source) return;
        _ = StartProbeAfterStopAsync(source.Id, request);
    }

    private async Task StartProbeAfterStopAsync(string inputId, long request)
    {
        try { await Task.WhenAll(stopping, probeStopping); }
        catch { /* Ignored during cleanup. */ }
        if (disposed || request != probeRevision || !homePageActive || engine is not null || starting || enumerating) return;
        if (SelectedInput?.Id != inputId) return;

        WasapiMicMeter? meter = null;
        meter = new WasapiMicMeter(inputId, error => DispatcherQueue.TryEnqueue(() =>
        {
            if (disposed || !ReferenceEquals(probe, meter)) return;
            StopProbe();
            if (error.HResult == unchecked((int)0x80070005))
                status.Text = "无法访问麦克风。请在 Windows 隐私设置中允许桌面应用使用麦克风。";
            else if (error.HResult == unchecked((int)0x8889000A))
                status.Text = "麦克风正被其他程序独占占用，无法试麦预览电平。";
            NotifyStateChanged();
        }));
        probe = meter;
        probeStopping = meter.Finished;
        meter.Start();
        NotifyStateChanged();
    }

    private void StopProbe()
    {
        var current = probe;
        probe = null;
        if (current is not null)
        {
            current.Dispose();
            probeStopping = current.Finished;
        }
    }

    private void NotifyStateChanged()
    {
        if (!disposed && !suppressNotifications) StateChanged?.Invoke();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        homePageActive = false;
        probeRevision++;
        StopProbe();
        Stop();
    }
}
