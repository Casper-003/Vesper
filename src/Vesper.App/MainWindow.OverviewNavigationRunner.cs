using System.Text.Json;

namespace Vesper.App;

public sealed partial class MainWindow
{
    private async Task RunOverviewNavigationChecksAsync(string reportPath)
    {
        canSave = false;
        var failures = new List<string>();
        object? result = null;
        try { result = await CheckOverviewNavigationAsync(failures); }
        catch (Exception ex) { failures.Add(ex.ToString()); }
        try
        {
            string path = Path.GetFullPath(reportPath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
            {
                Passed = failures.Count == 0, Result = result, Failures = failures,
                Limitations = "Isolated synthetic Frame navigation and Root render samples. No real library, online lookups, desktop automation or manual visual acceptance. Native Mica is replaced only during pixel capture."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { Close(); }
    }
}
