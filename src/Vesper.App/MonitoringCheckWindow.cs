using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Vesper.App;

// Explicit developer entry. Queries endpoint metadata, never starts microphones.
internal sealed class MonitoringCheckWindow : Window
{
    private bool started;
    internal MonitoringCheckWindow(string reportPath, bool initialize = false)
    {
        var content = new TextBlock { Text = "正在检查 WASAPI 设备接口（不采集音频）", Margin = new Thickness(24) };
        Content = content;
        content.Loaded += async (_, _) =>
        {
            if (started) return; started = true;
            string? error = null; object[] inputs = [], outputs = [], initialization = [];
            try
            {
                (inputs, outputs) = await Task.Run(() =>
                    (WasapiMonitoring.Devices(true).Select(WasapiMonitoring.Probe).ToArray(),
                     WasapiMonitoring.Devices(false).Select(WasapiMonitoring.Probe).ToArray()));
                if (initialize)
                    initialization = await Task.Run(() =>
                    {
                        var source = WasapiMonitoring.Devices(true).FirstOrDefault();
                        var devices = WasapiMonitoring.Devices(false);
                        var destination = devices.FirstOrDefault(d => d.Name.Contains("Realtek(R) Audio") && !d.Name.Contains("Digital"))
                            ?? devices.FirstOrDefault() ?? throw new NotSupportedException("没有可用的输出设备。");
                        return new[] { WasapiMonitoring.ProbeInitialization(source, destination, MonitoringMode.LowLatency),
                            WasapiMonitoring.ProbeInitialization(source, destination, MonitoringMode.Exclusive) };
                    });
            }
            catch (Exception ex) { error = ex.ToString(); }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new
            {
                Passed = error is null, Inputs = inputs, Outputs = outputs, Initialization = initialization, Error = error,
                Limits = initialize ? "显式初始化探测：尝试打开输入/输出流后立即释放，不调用 Start、不读取麦克风样本。各模式是否可用须看 Available，不代表返听听感、总延迟或实时稳定性。"
                    : "只枚举与查询 COM 音频接口和 RAW/引擎周期，不初始化或启动采集；不是麦克风返听、真实往返延迟、拔插及长时间稳定性验收。"
            }, new JsonSerializerOptions { WriteIndented = true }));
            Close();
        };
    }
}
