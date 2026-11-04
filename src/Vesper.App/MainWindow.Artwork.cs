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
    private sealed class CoverRead(Task work, CancellationToken owner)
    {
        internal Task Work { get; } = work;
        internal CancellationToken Owner { get; } = owner;
    }
    private readonly Dictionary<string, CoverRead> coverLoads = [];
    private readonly SemaphoreSlim coverSlots = new(2, 2);
    private readonly ArtworkDiskCache artworkCache = new(lookupHttp, ArtworkCacheDirectory());
    private static string ArtworkCacheDirectory()
    {
        string? report = Environment.GetEnvironmentVariable("VESPER_ARTWORK_CHECK_PATH")
            ?? Environment.GetEnvironmentVariable("VESPER_PAGE_CHECK_PATH")
            ?? Environment.GetEnvironmentVariable("VESPER_COLOR_CHECK_PATH");
        if (report is { Length: > 0 })
            return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(report))!, "artwork-check-cache-" + Environment.ProcessId);
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vesper", "Artwork");
    }
    private async Task LoadCoverAsync(LibrarySongItem song, CancellationToken token = default)
    {
        for (int generation = 0; generation < 3; generation++)
        {
            token.ThrowIfCancellationRequested();
            if (closed || song.Cover is not null) return;
            string identity = song.ArtworkIdentity;
            string key = song.Song.Id + "\n" + identity;
            if (coverLoads.TryGetValue(key, out var pending))
            {
                // The waiter only cancels its own wait. The existing owner's read
                // continues unless that owner's navigation or window is canceled.
                try { await pending.Work.WaitAsync(token); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
                token.ThrowIfCancellationRequested();
                if (song.ArtworkIdentity != identity) continue;
                if (song.Cover is not null || !pending.Owner.IsCancellationRequested) return;
                // A canceled page preparation may finish without producing art.
                // It must not make the next owner treat the missing bitmap as ready.
                if (coverLoads.GetValueOrDefault(key) == pending) coverLoads.Remove(key);
            }
            var read = new CoverRead(ReadCoverAsync(song, identity, token), token);
            coverLoads[key] = read;
            _ = ForgetCoverLoadAsync(key, read);
            await read.Work.WaitAsync(token);
            token.ThrowIfCancellationRequested();
            if (song.ArtworkIdentity != identity) continue;
            // An unillustrated video or absent embedded image is a settled placeholder.
            return;
        }
    }
    private async Task ForgetCoverLoadAsync(string key, CoverRead read)
    {
        await read.Work;
        if (coverLoads.GetValueOrDefault(key) == read) coverLoads.Remove(key);
    }

    private async Task ReadCoverAsync(LibrarySongItem song, string identity, CancellationToken token)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(metadataCancellation.Token, token);
        var readToken = operation.Token;
        string coverUrl = song.CoverUrl;
        bool entered = false;
        bool Current() => !closed && !readToken.IsCancellationRequested && song.ArtworkIdentity == identity;
        try
        {
            await coverSlots.WaitAsync(readToken); entered = true;
            if (!Current() || song.Cover is not null) return;
            byte[]? bytes = null;
            if (Uri.TryCreate(coverUrl, UriKind.Absolute, out var url) && ArtworkDiskCache.IsAllowed(url))
            {
                var larger = coverUrl.EndsWith("/front-500", StringComparison.Ordinal) ? new Uri(coverUrl[..^3] + "1200") : url;
                try { bytes = await DownloadCoverAsync(larger, readToken); }
                catch (Exception) when (larger != url && !readToken.IsCancellationRequested)
                { bytes = await DownloadCoverAsync(url, readToken); }
            }
            else
            {
                string? path = await Task.Run(() => song.Song.AvailablePath, readToken);
                if (path is null || IsVideoFile(path)) return;
                var file = await StorageFile.GetFileFromPathAsync(path).AsTask(readToken);
                using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.MusicView, 600,
                    ThumbnailOptions.UseCurrentScale).AsTask(readToken);
                if (thumbnail is null || thumbnail.Type != ThumbnailType.Image) return;
                using var source = thumbnail.AsStreamForRead();
                bytes = await ReadCoverBytesAsync(source, readToken);
            }
            if (bytes is null || !Current()) return;
            // Download once: the same bytes feed a tiny palette decode and the displayed artwork.
            uint color = await Task.Run(async () =>
            {
                using var stream = new MemoryStream(bytes).AsRandomAccessStream();
                var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(readToken);
                var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight,
                    new BitmapTransform { ScaledWidth = 64, ScaledHeight = 64 }, ExifOrientationMode.IgnoreExifOrientation,
                    ColorManagementMode.DoNotColorManage).AsTask(readToken);
                return CoverPalette.DominantArgb(pixels.DetachPixelData());
            }, readToken);
            if (!Current()) return;
            using var imageStream = new MemoryStream(bytes).AsRandomAccessStream();
            var image = new BitmapImage { DecodePixelWidth = 1200 };
            await image.SetSourceAsync(imageStream).AsTask(readToken);
            if (Current())
            {
                RememberArtworkSwatch(image, color);
                song.CoverColor = color; song.Cover = image;
            }
        }
        catch (Exception) { /* Failed artwork keeps a neutral header and the existing placeholder. */ }
        finally { if (entered) coverSlots.Release(); }
    }
    private async Task<byte[]> DownloadCoverAsync(Uri url)
        => await DownloadCoverAsync(url, metadataCancellation.Token);
    private async Task<byte[]> DownloadCoverAsync(Uri url, CancellationToken token)
    {
        string path = await artworkCache.GetAsync(url, token);
        return await File.ReadAllBytesAsync(path, token);
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
