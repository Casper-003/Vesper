using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;
using Vesper.Core;

namespace Vesper.App;

public sealed class CoverColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        uint color = value is uint argb ? argb : 0xFF48545C;
        return new SolidColorBrush(Windows.UI.Color.FromArgb(255, (byte)(color >> 16), (byte)(color >> 8), (byte)color));
    }
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

public sealed partial class MainWindow
{
    private readonly Dictionary<string, Task> coverLoads = [];
    private readonly SemaphoreSlim coverSlots = new(2, 2);
    private Task LoadCoverAsync(LibrarySongItem song)
    {
        if (closed || song.Cover is not null) return Task.CompletedTask;
        string key = song.Song.Id + "\n" + song.CoverUrl;
        if (coverLoads.TryGetValue(key, out var pending) && !pending.IsCompleted) return pending;
        return coverLoads[key] = ReadCoverAsync(song);
    }

    private async Task ReadCoverAsync(LibrarySongItem song)
    {
        string identity = song.CoverUrl;
        bool entered = false;
        try
        {
            await coverSlots.WaitAsync(metadataCancellation.Token); entered = true;
            byte[]? bytes = null;
            if (Uri.TryCreate(identity, UriKind.Absolute, out var url) && url.Scheme == "https"
                && (url.Host == "coverartarchive.org" || url.Host.EndsWith(".music.126.net", StringComparison.OrdinalIgnoreCase)
                    || url.Host.EndsWith(".dzcdn.net", StringComparison.OrdinalIgnoreCase)))
            {
                var larger = identity.EndsWith("/front-500", StringComparison.Ordinal) ? new Uri(identity[..^3] + "1200") : url;
                try { bytes = await DownloadCoverAsync(larger); }
                catch (Exception) when (larger != url && !metadataCancellation.IsCancellationRequested)
                { bytes = await DownloadCoverAsync(url); }
            }
            else
            {
                string? path = await Task.Run(() => song.Song.AvailablePath, metadataCancellation.Token);
                if (path is null || IsVideoFile(path)) return;
                var file = await StorageFile.GetFileFromPathAsync(path);
                using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.MusicView, 600,
                    ThumbnailOptions.UseCurrentScale).AsTask(metadataCancellation.Token);
                if (thumbnail is null || thumbnail.Type != ThumbnailType.Image) return;
                using var source = thumbnail.AsStreamForRead();
                bytes = await ReadCoverBytesAsync(source, metadataCancellation.Token);
            }
            if (bytes is null || closed || song.CoverUrl != identity) return;
            // Download once: the same bytes feed a tiny palette decode and the displayed artwork.
            uint color = await Task.Run(async () =>
            {
                using var stream = new MemoryStream(bytes).AsRandomAccessStream();
                var decoder = await BitmapDecoder.CreateAsync(stream);
                var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight,
                    new BitmapTransform { ScaledWidth = 64, ScaledHeight = 64 }, ExifOrientationMode.IgnoreExifOrientation,
                    ColorManagementMode.DoNotColorManage);
                return CoverPalette.DominantArgb(pixels.DetachPixelData());
            }, metadataCancellation.Token);
            using var imageStream = new MemoryStream(bytes).AsRandomAccessStream();
            var image = new BitmapImage { DecodePixelWidth = 1200 };
            await image.SetSourceAsync(imageStream);
            if (!closed && song.CoverUrl == identity) { song.CoverColor = color; song.Cover = image; }
        }
        catch (Exception) { /* Failed artwork keeps a neutral header and the existing placeholder. */ }
        finally { if (entered) coverSlots.Release(); }
    }
    private async Task<byte[]> DownloadCoverAsync(Uri url)
    {
        string cacheDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vesper", "Artwork");
        string key = System.Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(url.AbsoluteUri)));
        string cachePath = Path.Combine(cacheDirectory, key + ".image");
        try
        {
            if (File.Exists(cachePath) && new FileInfo(cachePath).Length is > 0 and <= 8388608)
                return await File.ReadAllBytesAsync(cachePath, metadataCancellation.Token);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(metadataCancellation.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var response = await lookupHttp.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        byte[] bytes = await ReadCoverBytesAsync(stream, timeout.Token);
        string temporary = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(cacheDirectory);
            await File.WriteAllBytesAsync(temporary, bytes, timeout.Token);
            File.Move(temporary, cachePath, overwrite: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return bytes;
    }
    private static async Task<byte[]> ReadCoverBytesAsync(Stream stream, CancellationToken token)
    {
        using var output = new MemoryStream();
        byte[] buffer = new byte[16384]; int count;
        while ((count = await stream.ReadAsync(buffer, token)) > 0)
        {
            if (output.Length + count > 8 * 1024 * 1024) throw new InvalidDataException("Artwork too large.");
            await output.WriteAsync(buffer.AsMemory(0, count), token);
        }
        return output.ToArray();
    }
}
