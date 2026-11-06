using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;

namespace Vesper.App;

internal sealed class MonitoringPanel : UserControl, IDisposable
{
    private readonly ComboBox input = new() { Header = "麦克风", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox output = new() { Header = "人声输出设备", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox mode = new() { Header = "音频模式", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Slider volume = new() { Header = "返听音量", Minimum = 0, Maximum = 100, Value = 20 };
    private readonly Button toggle = new() { Content = "开启实时返听", IsEnabled = false };
    private readonly Button refresh = new() { Content = "刷新设备" };
    private readonly TextBlock status = new() { Text = "返听已关闭", TextWrapping = TextWrapping.Wrap };
    private WasapiMonitoring? engine;
    private Task stopping = Task.CompletedTask;
    private long revision;
    private bool disposed, enumerating, loaded;

    internal MonitoringPanel()
    {
        var layout = new StackPanel { Spacing = 12, MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Left };
        Content = layout; HorizontalContentAlignment = HorizontalAlignment.Stretch;
        layout.Children.Add(new TextBlock { Text = "实时人声返听", FontSize = 20, Margin = new Thickness(0, 20, 0, 0) });
        layout.Children.Add(new TextBlock { Text = "将麦克风人声实时送到耳机或音箱。使用音箱时请从较低音量开始，避免麦克风与音箱形成啸叫。", TextWrapping = TextWrapping.Wrap });
        mode.Items.Add(new ComboBoxItem { Content = "共享低延迟" });
        mode.Items.Add(new ComboBoxItem { Content = "兼容共享" });
        mode.Items.Add(new ComboBoxItem { Content = "独占（待伴奏统一输出接入）", IsEnabled = false });
        mode.SelectedIndex = 0;
        foreach (UIElement element in new UIElement[] { input, output, mode, volume, toggle, refresh, status }) layout.Children.Add(element);
        AutomationProperties.SetName(input, "返听麦克风"); AutomationProperties.SetName(output, "人声输出设备");
        AutomationProperties.SetName(volume, "返听音量");
        toggle.Click += async (_, _) => { if (engine is null) await StartAsync(); else Stop(); };
        refresh.Click += async (_, _) => await RefreshDevicesAsync();
        input.SelectionChanged += (_, _) => ConfigurationChanged();
        output.SelectionChanged += (_, _) => ConfigurationChanged();
        mode.SelectionChanged += (_, _) => ConfigurationChanged();
        volume.ValueChanged += (_, _) => { if (engine is { } current) current.Gain = (float)(volume.Value / 100); };
        Loaded += async (_, _) => { if (!loaded) { loaded = true; await RefreshDevicesAsync(); } };
    }
    private void ConfigurationChanged()
    {
        if (engine is not null) Stop();
        else revision++; // Also invalidate a start waiting for the prior worker to stop.
        toggle.IsEnabled = !enumerating && input.SelectedItem is MonitoringDevice && output.SelectedItem is MonitoringDevice;
    }
    private async Task RefreshDevicesAsync()
    {
        if (disposed || enumerating || engine is not null) return;
        enumerating = true; refresh.IsEnabled = toggle.IsEnabled = false;
        try
        {
            string? selectedInput = (input.SelectedItem as MonitoringDevice)?.Id;
            string? selectedOutput = (output.SelectedItem as MonitoringDevice)?.Id;
            var devices = await Task.Run(() => (Inputs: WasapiMonitoring.Devices(true), Outputs: WasapiMonitoring.Devices(false)));
            if (disposed) return;
            input.ItemsSource = devices.Inputs; output.ItemsSource = devices.Outputs;
            input.SelectedItem = devices.Inputs.FirstOrDefault(d => d.Id == selectedInput) ?? devices.Inputs.FirstOrDefault();
            output.SelectedItem = devices.Outputs.FirstOrDefault(d => d.Id == selectedOutput) ?? devices.Outputs.FirstOrDefault();
            status.Text = devices.Inputs.Count == 0 ? "没有可用的麦克风，请连接后刷新。"
                : devices.Outputs.Count == 0 ? "没有可用的输出设备，请连接后刷新。" : "返听已关闭，请确认麦克风和人声输出设备后开启。";
        }
        catch (Exception) { if (!disposed) status.Text = "无法读取音频设备，请检查 Windows 音频服务后重试。"; }
        finally { enumerating = false; if (!disposed) { refresh.IsEnabled = true; ConfigurationChanged(); } }
    }
    private async Task StartAsync()
    {
        if (disposed || input.SelectedItem is not MonitoringDevice source || output.SelectedItem is not MonitoringDevice destination) return;
        long request = ++revision;
        MonitoringMode selectedMode = mode.SelectedIndex == 0 ? MonitoringMode.LowLatency : MonitoringMode.Compatible;
        toggle.IsEnabled = false; refresh.IsEnabled = false; status.Text = "正在准备实时返听…";
        try
        {
            await stopping;
            if (request != revision || disposed) return;
            WasapiMonitoring? current = null;
            current = new(source.Id, destination.Id, selectedMode,
                error => DispatcherQueue.TryEnqueue(() =>
                {
                    if (disposed || !ReferenceEquals(engine, current)) return;
                    Stop(); status.Text = Describe(error);
                }));
            engine = current; current.Gain = (float)(volume.Value / 100);
            toggle.Content = "关闭实时返听"; toggle.IsEnabled = true;
            current.Start();
            MonitoringInfo info = await current.Started;
            if (request == revision && !disposed) status.Text = info.ToString();
        }
        catch (Exception error)
        {
            if (request != revision || disposed) return;
            Stop(); status.Text = Describe(error);
        }
        finally { if (request == revision && !disposed) { toggle.IsEnabled = true; refresh.IsEnabled = engine is null; } }
    }
    private static string Describe(Exception error) => error is NotSupportedException ? error.Message
        : error.HResult == unchecked((int)0x80070005) ? "无法访问麦克风。请在 Windows 隐私设置中允许桌面应用使用麦克风。"
        : $"返听已停止（{error.HResult:X8}）。请检查设备连接；低延迟模式不可用时，可选择兼容共享后重试。";
    private void Stop()
    {
        revision++;
        var current = engine; engine = null;
        if (current is not null) { current.Dispose(); stopping = current.Finished; }
        toggle.Content = "开启实时返听"; status.Text = "返听已关闭"; refresh.IsEnabled = true;
    }
    public void Dispose() { if (disposed) return; disposed = true; Stop(); }
}
