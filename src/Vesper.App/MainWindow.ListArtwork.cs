using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;

namespace Vesper.App;

public sealed partial class MainWindow
{
    private readonly Dictionary<FrameworkElement, (LibrarySongItem Song, PropertyChangedEventHandler Handler)> artworkRows = [];
    private readonly Dictionary<string, Task> listCoverLoads = [];
    private readonly Dictionary<string, DateTimeOffset> listCoverRetries = [];
    private readonly LinkedList<LibrarySongItem> listCoverCache = new();
    private const int ListCoverCacheCapacity = 96;
    private const int ListCoverDecodeWidth = 128;

    private void ObserveSongRowArtwork(FrameworkElement row)
    {
        if (!row.IsLoaded) { ForgetSongRowArtwork(row); return; }
        if (artworkRows.TryGetValue(row, out var previous) && ReferenceEquals(previous.Song, row.DataContext)) return;
        ForgetSongRowArtwork(row);
        if (row.DataContext is not LibrarySongItem song) return;
        PropertyChangedEventHandler handler = (_, args) =>
        {
            if (args.PropertyName is nameof(LibrarySongItem.ArtworkIdentity) or nameof(LibrarySongItem.CoverUrl))
                QueueVisibleRowMetadata(row);
        };
        artworkRows[row] = (song, handler);
        song.PropertyChanged += handler;
    }

    private void ForgetSongRowArtwork(FrameworkElement row)
    {
        pendingMetadataRows.Remove(row);
        if (artworkRows.Remove(row, out var previous)) previous.Song.PropertyChanged -= previous.Handler;
    }

    private void QueueVisibleArtworkForSong(string songId)
    {
        foreach (var key in homeArtworkRetries.Keys.Where(key => key.StartsWith(songId + "\n", StringComparison.Ordinal)).ToArray())
            homeArtworkRetries.Remove(key);
        ScheduleHomeArtwork();
        foreach (var pair in artworkRows)
        {
            if (!pair.Key.IsLoaded || pair.Value.Song.Song.Id != songId) continue;
            listCoverRetries.Remove(songId + "\n" + pair.Value.Song.ArtworkIdentity);
            QueueVisibleRowMetadata(pair.Key);
        }
    }

    private void QueueCurrentVisibleArtwork()
    {
        foreach (var pair in artworkRows)
            if (pair.Key.IsLoaded && IsSongVisible(pair.Value.Song)) QueueVisibleRowMetadata(pair.Key);
        TrimListCoverCache();
    }

    private void OnSongRowUnloaded(object sender, RoutedEventArgs args)
    { if (sender is FrameworkElement row) ForgetSongRowArtwork(row); }

    private async Task LoadVisibleRowPresentationAsync(LibrarySongItem song)
    {
        await LoadSongMetadataAsync(song, visibleOnly: true);
        if (!closed && IsSongVisible(song)) await LoadListCoverAsync(song);
    }

    private Task LoadListCoverAsync(LibrarySongItem song, bool prepare = false, CancellationToken token = default)
    {
        if (prepare) return PrepareListCoverAsync(song, token);
        if (closed || !IsSongVisible(song)) return Task.CompletedTask;
        if (song.ListCover is not null)
        {
            if (listCoverCache.Remove(song)) listCoverCache.AddLast(song);
            return Task.CompletedTask;
        }
        string identity = song.ArtworkIdentity;
        string key = song.Song.Id + "\n" + identity;
        if (listCoverRetries.TryGetValue(key, out var retryAt) && retryAt > DateTimeOffset.UtcNow) return Task.CompletedTask;
        if (listCoverLoads.TryGetValue(key, out var pending)) return pending.WaitAsync(token);
        return StartListCoverRead(song, identity, key, prepare: false, token);
    }

    private Task StartListCoverRead(LibrarySongItem song, string identity, string key, bool prepare, CancellationToken token)
    {
        var task = ReadListCoverAsync(song, identity, key, prepare, token);
        listCoverLoads[key] = task;
        _ = ForgetListCoverLoadAsync(key, task);
        return task;
    }

    private async Task PrepareListCoverAsync(LibrarySongItem song, CancellationToken token)
    {
        // Sharing a task does not guarantee it produced a bitmap: an older owner
        // can be canceled, or a visible-only row can scroll out while it waits.
        // A preparation may join that work, then make one read under its own token.
        for (int generation = 0; generation < 3; generation++)
        {
            token.ThrowIfCancellationRequested();
            if (closed) return;
            if (song.ListCover is not null)
            {
                if (listCoverCache.Remove(song)) listCoverCache.AddLast(song);
                return;
            }
            string identity = song.ArtworkIdentity;
            string key = song.Song.Id + "\n" + identity;
            if (listCoverRetries.TryGetValue(key, out var retryAt) && retryAt > DateTimeOffset.UtcNow) return;
            if (listCoverLoads.TryGetValue(key, out var pending))
            {
                try { await pending.WaitAsync(token); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { /* The old owner was canceled. */ }
                token.ThrowIfCancellationRequested();
                if (song.ArtworkIdentity != identity) continue;
                if (song.ListCover is not null) return;
                if (listCoverRetries.TryGetValue(key, out retryAt) && retryAt > DateTimeOffset.UtcNow) return;
                if (listCoverLoads.GetValueOrDefault(key) == pending) listCoverLoads.Remove(key);
            }
            await StartListCoverRead(song, identity, key, prepare: true, token).WaitAsync(token);
            token.ThrowIfCancellationRequested();
            if (song.ArtworkIdentity != identity) continue;
            // No URL, an unillustrated video or missing embedded art is a settled
            // placeholder; do not recurse forever waiting for an image that cannot exist.
            return;
        }
    }

    private async Task ForgetListCoverLoadAsync(string key, Task task)
    {
        await task;
        if (listCoverLoads.GetValueOrDefault(key) == task) listCoverLoads.Remove(key);
    }

    private async Task ReadListCoverAsync(LibrarySongItem song, string identity, string key, bool prepare, CancellationToken token)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(metadataCancellation.Token, token);
        var readToken = operation.Token;
        bool entered = false;
        bool Current() => !closed && !readToken.IsCancellationRequested && song.ArtworkIdentity == identity
            && (prepare || IsSongVisible(song));
        try
        {
            await coverSlots.WaitAsync(readToken); entered = true;
            if (!Current() || song.ListCover is not null) return;
            byte[]? bytes;
            if (Uri.TryCreate(song.CoverUrl, UriKind.Absolute, out var url) && Vesper.Core.ArtworkDiskCache.IsAllowed(url))
            {
                // Reuse the large encoded disk file if startup prefetch already has it;
                // a list row only decodes 128 pixels, irrespective of the source size.
                if (song.CoverUrl.EndsWith("/front-500", StringComparison.Ordinal))
                {
                    var larger = new Uri(song.CoverUrl[..^3] + "1200");
                    if (await Task.Run(() => File.Exists(artworkCache.GetCachePath(larger)), readToken)) url = larger;
                }
                bytes = await DownloadCoverAsync(url, readToken);
            }
            else
            {
                string? path = await Task.Run(() => song.Song.AvailablePath, readToken);
                if (path is null || IsVideoFile(path)) return;
                var file = await StorageFile.GetFileFromPathAsync(path);
                using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.MusicView, ListCoverDecodeWidth,
                    ThumbnailOptions.ResizeThumbnail).AsTask(readToken);
                if (thumbnail is null || thumbnail.Type != ThumbnailType.Image) return;
                using var source = thumbnail.AsStreamForRead();
                bytes = await ReadCoverBytesAsync(source, readToken);
            }
            if (!Current() || song.ListCover is not null) return;
            using var stream = new MemoryStream(bytes).AsRandomAccessStream();
            var image = new BitmapImage { DecodePixelWidth = ListCoverDecodeWidth, DecodePixelHeight = ListCoverDecodeWidth };
            await image.SetSourceAsync(stream);
            if (!Current() || song.ListCover is not null) return;
            song.Thumbnail = image;
            listCoverRetries.Remove(key);
            listCoverCache.Remove(song); listCoverCache.AddLast(song);
            TrimListCoverCache();
        }
        catch (Exception) when (!readToken.IsCancellationRequested)
        {
            if (listCoverRetries.Count >= ListCoverCacheCapacity) listCoverRetries.Remove(listCoverRetries.Keys.First());
            listCoverRetries[key] = DateTimeOffset.UtcNow.AddMinutes(1);
        }
        catch (OperationCanceledException) { }
        finally { if (entered) coverSlots.Release(); }
    }

    private void TrimListCoverCache()
    {
        var node = listCoverCache.First;
        while (listCoverCache.Count > ListCoverCacheCapacity && node is not null)
        {
            var next = node.Next;
            if (!IsSongVisible(node.Value))
            {
                node.Value.Thumbnail = null;
                listCoverCache.Remove(node);
            }
            node = next;
        }
    }

    private void CloseListArtwork()
    {
        foreach (var row in artworkRows.Keys.ToArray()) ForgetSongRowArtwork(row);
        foreach (var song in listCoverCache) song.Thumbnail = null;
        listCoverCache.Clear(); listCoverRetries.Clear();
    }
}
