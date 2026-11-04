using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;
using Vesper.Core;

namespace Vesper.App;

public sealed partial class MainWindow
{
    private sealed class AlbumArtworkEntry(AlbumArtworkSource source)
    {
        internal readonly AlbumArtworkSource Source = source;
        internal WeakReference<BitmapImage>? Image;
        internal Task<BitmapImage?>? Pending;
        internal DateTimeOffset RetryAt;
    }

    private readonly Dictionary<string, AlbumArtworkEntry> albumArtworkEntries = [];
    private readonly LinkedList<(string AlbumId, BitmapImage Image)> albumArtworkCache = new();
    private const int AlbumArtworkCacheCapacity = 8;

    private AlbumArtworkSource? GetAlbumArtworkSource(CatalogAlbum album, IReadOnlyList<CatalogTrack> tracks) =>
        AlbumArtwork.Choose(album, tracks.Select(track => viewModel.FindSong(track.SongId)).OfType<LibrarySongItem>()
            .DistinctBy(song => song.Song.Id).Select(song => new AlbumArtworkCandidate(song.Song.Id, song.ArtworkIdentity,
                song.Song.Sources.Any(path => !IsVideoFile(path)))));

    private BitmapImage? GetAlbumImage(CatalogAlbum album, IReadOnlyList<CatalogTrack> tracks)
    {
        var pending = EnsureAlbumArtworkAsync(album, tracks);
        return pending.IsCompletedSuccessfully ? pending.Result : null;
    }

    // Call on the UI dispatcher. Cancellation belongs to the page's wait, while an
    // identical active album request remains shared until the window lifetime ends.
    private Task<BitmapImage?> EnsureAlbumArtworkAsync(CatalogAlbum album, IReadOnlyList<CatalogTrack> tracks,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (closed || GetAlbumArtworkSource(album, tracks) is not { } source) return Task.FromResult<BitmapImage?>(null);
        if (!albumArtworkEntries.TryGetValue(album.Id, out var entry) || entry.Source.Key != source.Key)
        {
            entry = new(source);
            albumArtworkEntries[album.Id] = entry;
            RemoveStrongAlbumImage(album.Id);
        }
        if (entry.Image?.TryGetTarget(out var image) == true)
        {
            RememberAlbumImage(album.Id, image);
            return Task.FromResult<BitmapImage?>(image);
        }
        if (entry.Pending is { } active) return token.CanBeCanceled ? active.WaitAsync(token) : active;
        if (entry.RetryAt > DateTimeOffset.UtcNow) return Task.FromResult<BitmapImage?>(null);
        var task = ReadAlbumArtworkAsync(album.Id, entry);
        entry.Pending = task;
        _ = ForgetAlbumArtworkLoadAsync(entry, task);
        return token.CanBeCanceled ? task.WaitAsync(token) : task;
    }

    private async Task ForgetAlbumArtworkLoadAsync(AlbumArtworkEntry entry, Task<BitmapImage?> task)
    {
        await task;
        if (ReferenceEquals(entry.Pending, task)) entry.Pending = null;
        // Keep weak handles for bitmaps already owned by a page/card, preventing a
        // harmless refresh from replacing a still-painted cover with a placeholder.
        if (albumArtworkEntries.Count <= 128) return;
        foreach (string id in albumArtworkEntries.Where(pair => pair.Value.Pending is null
            && pair.Value.Image?.TryGetTarget(out _) != true && pair.Value.RetryAt <= DateTimeOffset.UtcNow)
            .Select(pair => pair.Key).ToArray()) albumArtworkEntries.Remove(id);
    }

    private async Task<BitmapImage?> ReadAlbumArtworkAsync(string albumId, AlbumArtworkEntry entry)
    {
        bool entered = false;
        bool Current() => !closed && ReferenceEquals(albumArtworkEntries.GetValueOrDefault(albumId), entry);
        try
        {
            await coverSlots.WaitAsync(metadataCancellation.Token); entered = true;
            if (!Current()) return null;
            byte[] bytes;
            if (entry.Source.CoverUrl.Length > 0)
            {
                var url = new Uri(entry.Source.CoverUrl);
                var larger = entry.Source.CoverUrl.EndsWith("/front-500", StringComparison.Ordinal)
                    ? new Uri(entry.Source.CoverUrl[..^3] + "1200") : url;
                try { bytes = await DownloadCoverAsync(larger); }
                catch (Exception) when (larger != url && !metadataCancellation.IsCancellationRequested)
                { bytes = await DownloadCoverAsync(url); }
            }
            else
            {
                var song = viewModel.FindSong(entry.Source.SongId);
                if (song is null || song.ArtworkIdentity != entry.Source.LocalIdentity) return null;
                var source = song.Song;
                string? path = await Task.Run(() => source.Sources.FirstOrDefault(path => !IsVideoFile(path) && File.Exists(path)),
                    metadataCancellation.Token);
                if (path is null) { entry.RetryAt = DateTimeOffset.UtcNow.AddMinutes(1); return null; }
                var file = await StorageFile.GetFileFromPathAsync(path);
                using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.MusicView, 600,
                    ThumbnailOptions.ResizeThumbnail).AsTask(metadataCancellation.Token);
                if (thumbnail is null || thumbnail.Type != ThumbnailType.Image)
                { entry.RetryAt = DateTimeOffset.UtcNow.AddDays(1); return null; }
                using var stream = thumbnail.AsStreamForRead();
                bytes = await ReadCoverBytesAsync(stream, metadataCancellation.Token);
                if (viewModel.FindSong(entry.Source.SongId)?.ArtworkIdentity != entry.Source.LocalIdentity) return null;
            }
            if (!Current()) return null;
            uint swatch = await ReadArtworkSwatchAsync(bytes, metadataCancellation.Token);
            if (!Current()) return null;
            using var imageStream = new MemoryStream(bytes).AsRandomAccessStream();
            var image = new BitmapImage { DecodePixelWidth = 600, DecodePixelHeight = 600 };
            await image.SetSourceAsync(imageStream);
            if (!Current()) return null;
            RememberArtworkSwatch(image, swatch);
            entry.Image = new(image);
            RememberAlbumImage(albumId, image);
            RequestEntityRefresh();
            return image;
        }
        catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Gone)
        { entry.RetryAt = DateTimeOffset.UtcNow.AddDays(1); }
        catch (Exception) when (!metadataCancellation.IsCancellationRequested)
        { entry.RetryAt = DateTimeOffset.UtcNow.AddMinutes(1); }
        catch (OperationCanceledException) { }
        finally { if (entered) coverSlots.Release(); }
        return null;
    }

    private void RememberAlbumImage(string albumId, BitmapImage image)
    {
        RemoveStrongAlbumImage(albumId);
        albumArtworkCache.AddLast((albumId, image));
        while (albumArtworkCache.Count > AlbumArtworkCacheCapacity) albumArtworkCache.RemoveFirst();
    }

    private void RemoveStrongAlbumImage(string albumId)
    {
        for (var node = albumArtworkCache.First; node is not null; node = node.Next)
            if (node.Value.AlbumId == albumId) { albumArtworkCache.Remove(node); return; }
    }
}
