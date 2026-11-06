using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;

namespace Vesper.App;

public sealed partial class MainWindow
{
    private sealed record HomeArtworkRead(Task<BitmapImage?> Work, CancellationToken Owner);
    private readonly Dictionary<string, HomeArtworkRead> homeArtworkReads = [];
    private readonly LinkedList<(string Key, BitmapImage Image)> homeSongImages = [];
    private readonly Dictionary<string, DateTimeOffset> homeArtworkRetries = [];

    private async Task<BitmapImage?> ReadHomeArtworkAsync(LibrarySongItem song, CancellationToken token)
    {
        string identity = song.ArtworkIdentity, key = song.Song.Id + "\n" + identity;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (closed || song.ArtworkIdentity != identity) return null;
            var cached = homeSongImages.First;
            while (cached is not null && cached.Value.Key != key) cached = cached.Next;
            if (cached is not null) { homeSongImages.Remove(cached); homeSongImages.AddLast(cached); return cached.Value.Image; }
            if (homeArtworkRetries.TryGetValue(key, out var retry) && retry > DateTimeOffset.UtcNow) return null;
            if (homeArtworkReads.TryGetValue(key, out var pending))
            {
                var image = await pending.Work.WaitAsync(token);
                if (image is not null || !pending.Owner.IsCancellationRequested) return image;
                if (homeArtworkReads.GetValueOrDefault(key) == pending) homeArtworkReads.Remove(key);
                continue;
            }
            var read = new HomeArtworkRead(DecodeHomeArtworkAsync(song, identity, key, token), token);
            homeArtworkReads[key] = read; _ = ForgetHomeArtworkAsync(key, read);
            return await read.Work.WaitAsync(token);
        }
    }
    private async Task ForgetHomeArtworkAsync(string key, HomeArtworkRead read)
    { await read.Work; if (homeArtworkReads.GetValueOrDefault(key) == read) homeArtworkReads.Remove(key); }

    private async Task<BitmapImage?> DecodeHomeArtworkAsync(LibrarySongItem song, string identity, string key, CancellationToken token)
    {
        bool entered = false;
        try
        {
            await coverSlots.WaitAsync(token); entered = true;
            byte[]? bytes = null;
            if (Uri.TryCreate(song.CoverUrl, UriKind.Absolute, out var url) && Vesper.Core.ArtworkDiskCache.IsAllowed(url))
            {
                if (song.CoverUrl.EndsWith("/front-500", StringComparison.Ordinal))
                {
                    var larger = new Uri(song.CoverUrl[..^3] + "1200");
                    if (File.Exists(artworkCache.GetCachePath(larger))) url = larger;
                }
                bytes = await DownloadCoverAsync(url, token);
            }
            else
            {
                string? path = await Task.Run(() => song.Song.AvailablePath, token);
                if (path is not null && !IsVideoFile(path))
                {
                    var file = await StorageFile.GetFileFromPathAsync(path).AsTask(token);
                    using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.MusicView, 320, ThumbnailOptions.ResizeThumbnail).AsTask(token);
                    if (thumbnail?.Type == ThumbnailType.Image)
                    { using var stream = thumbnail.AsStreamForRead(); bytes = await ReadCoverBytesAsync(stream, token); }
                }
            }
            token.ThrowIfCancellationRequested();
            if (closed || song.ArtworkIdentity != identity) return null;
            if (bytes is null) { RetryLater(); return null; }
            using var source = new MemoryStream(bytes).AsRandomAccessStream();
            var image = new BitmapImage { DecodePixelWidth = 320, DecodePixelHeight = 320 };
            await image.SetSourceAsync(source).AsTask(token);
            if (closed || token.IsCancellationRequested || song.ArtworkIdentity != identity) return null;
            homeSongImages.AddLast((key, image));
            while (homeSongImages.Count > 24) homeSongImages.RemoveFirst();
            homeArtworkRetries.Remove(key); return image;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception) { RetryLater(); return null; }
        finally { if (entered) coverSlots.Release(); }

        void RetryLater()
        {
            if (homeArtworkRetries.Count >= 64) homeArtworkRetries.Remove(homeArtworkRetries.Keys.First());
            homeArtworkRetries[key] = DateTimeOffset.UtcNow.AddMinutes(1);
        }
    }
}
