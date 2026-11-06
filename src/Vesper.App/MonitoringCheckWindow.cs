using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Vesper.App;

// Explicit developer entry. Queries endpoint metadata, never starts microphones.
internal sealed class MonitoringCheckWindow : Window
{
    private bool started;
    internal MonitoringCheckWindow(string reportPath)
    {
        var content = new TextBlock { Text = "正在检查 WASAPI 设备接口（不采集音频）", Margin = new Thickness(24) };
        Content = content;
        content.Loaded += async (_, _) =>
        {
            if (started) return; started = true;
            string? error = null; object[] inputs = [], outputs = [];
            try
            {
                (inputs, outputs) = await Task.Run(() =>
                    (WasapiMonitoring.Devices(true).Select(WasapiMonitoring.Probe).ToArray(),
                     WasapiMonitoring.Devices(false).Select(WasapiMonitoring.Probe).ToArray()));
            }
            catch (Exception ex) { error = ex.ToString(); }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new
            {
                Passed = error is null, Inputs = inputs, Outputs = outputs, Error = error,
                Limits = "只枚举与查询 COM 音频接口和 RAW/引擎周期，不初始化或启动采集；不是麦克风返听、真实往返延迟、拔插及长时间稳定性验收。"
            }, new JsonSerializerOptions { WriteIndented = true }));
            Close();
        };
    }
}
