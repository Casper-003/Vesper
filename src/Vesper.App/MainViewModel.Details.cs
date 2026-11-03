using Vesper.Core;

namespace Vesper.App;

public sealed partial class MainViewModel
{
    private bool presentationScrolling;
    private bool detailsRefreshPending;
    public event EventHandler? DetailsPresentationInvalidated;
    public void SetPresentationScrolling(bool scrolling)
    {
        presentationScrolling = scrolling;
        if (!scrolling) FlushDetailsPresentation();
    }
    public void FlushDetailsPresentation()
    {
        if (presentationScrolling || !detailsRefreshPending || disposed) return;
        detailsRefreshPending = false;
        Filter();
    }
    private void RefreshDetailsPresentation()
    {
        bool alreadyPending = detailsRefreshPending;
        detailsRefreshPending = true;
        if (presentationScrolling || alreadyPending) return;
        if (DetailsPresentationInvalidated is null) FlushDetailsPresentation();
        else DetailsPresentationInvalidated.Invoke(this, EventArgs.Empty);
    }
    private string ResolveSongId(string id)
    {
        var visited = new HashSet<string>();
        while (visited.Add(id) && songAliases.TryGetValue(id, out string? canonical)) id = canonical;
        return id;
    }

    public LibrarySongItem? FindSong(string id) => songItems.GetValueOrDefault(ResolveSongId(id));
    public SongMetadata GetSongMetadata(string id) => Library.Details.GetValueOrDefault(ResolveSongId(id))?.Effective("") ?? new();
    public SongLookupInput? GetLookupInput(string id)
    {
        var item = FindSong(id);
        if (item is null) return null;
        string path = item.Song.Sources.Concat(item.Song.KnownSources ?? []).FirstOrDefault() ?? item.Song.Title;
        string fileName = Path.GetFileName(path);
        return new(fileName, Library.Details.GetValueOrDefault(item.Song.Id) ?? new());
    }

    public Task<bool> SaveAutomaticLookupAsync(string id, SongLookupInput expected, SongLookupResult result) => SaveDetailsAsync(id,
        current => GetLookupInput(id)?.Key == expected.Key && current.Online == expected.Details.Online
            ? current with
            {
                Online = current.Online is null || current.Online.SourceUrl.Contains("lrclib.net", StringComparison.OrdinalIgnoreCase)
                    ? result.Metadata ?? current.Online : current.Online,
                OnlineLyrics = current.OnlineLyrics ?? result.Lyrics,
                Lookup = new(expected.Key, clock.GetUtcNow(), result.MetadataOutcome, result.LyricsOutcome)
            } : current,
        (current, previous, updated) => current with
        {
            Online = current.Online == updated.Online ? previous.Online : current.Online,
            OnlineLyrics = current.OnlineLyrics == updated.OnlineLyrics ? previous.OnlineLyrics : current.OnlineLyrics,
            Lookup = current.Lookup == updated.Lookup ? previous.Lookup : current.Lookup
        }, null);

    public void ApplyLocalMetadata(LibrarySongItem item, string path, SongMetadata metadata)
    {
        var current = FindSong(item.Song.Id);
        if (disposed || ReadOnly || current is null || !current.Song.Sources.Contains(path, StringComparer.OrdinalIgnoreCase)) return;
        string id = current.Song.Id;
        bool indexStarted;
        lock (searchIndexGate)
        {
            SongDetails previous = Library.Details.GetValueOrDefault(id) ?? new();
            Library.Details[id] = previous with { Local = metadata };
            indexStarted = MarkSearchIndexDirty();
        }
        bool titleChanged = current.Title != Library.Details[id].Effective(current.Song.Title).Title;
        current.ApplyDetails(Library.Details[id]);
        AnnounceSearchIndexBuild(indexStarted);
        if (titleChanged) RefreshDetailsPresentation();
    }

    public Task<bool> SaveCorrectionsAsync(string id, string title, string artist, string album)
    {
        if (string.IsNullOrWhiteSpace(title)) { ReportProblem("请填写歌名。"); return Task.FromResult(false); }
        return SaveDetailsAsync(id,
            previous =>
            {
                var old = previous.Effective("");
                bool changed = !(old.Title == title.Trim() || SongNameMatching.Same(old.Title, title.Trim()))
                    || !(old.Artist == artist.Trim() || SongNameMatching.SameArtist(old.Artist, artist.Trim()));
                return previous with { Corrections = new(title.Trim(), artist.Trim(), album.Trim(), clock.GetUtcNow()), Video = null,
                    Lookup = null, Online = changed ? null : previous.Online, OnlineLyrics = changed ? null : previous.OnlineLyrics };
            },
            (current, previous, updated) => current.Corrections == updated.Corrections
                ? current with { Corrections = previous.Corrections, Video = previous.Video, Lookup = previous.Lookup, Online = previous.Online, OnlineLyrics = previous.OnlineLyrics } : current,
            "歌曲资料已保存");
    }

    public Task<bool> SaveOnlineMetadataAsync(string id, SongMetadata metadata) => SaveDetailsAsync(id,
        previous => previous with { Online = metadata, Video = null, OnlineLyrics = null, Lookup = null },
        (current, previous, updated) => current.Online == updated.Online ? current with { Online = previous.Online, Video = previous.Video, OnlineLyrics = previous.OnlineLyrics, Lookup = previous.Lookup } : current,
        "已保存匹配资料");

    public Task<bool> SaveVideoMatchAsync(string id, MusicVideoMatch video, string expectedTitle, string expectedArtist) => SaveDetailsAsync(id,
        previous => previous.Effective("") is var metadata && metadata.Title == expectedTitle && metadata.Artist == expectedArtist
            ? previous with { Video = video } : previous,
        (current, previous, updated) => current.Video == updated.Video ? current with { Video = previous.Video } : current,
        "已找到匹配的 MV");

    private async Task<bool> SaveDetailsAsync(string id, Func<SongDetails, SongDetails> change,
        Func<SongDetails, SongDetails, SongDetails, SongDetails> rollback, string? message)
    {
        if (disposed || ReadOnly || !initialized) return false;
        await detailsWrites.WaitAsync();
        try
        {
            id = ResolveSongId(id);
            if (!songItems.ContainsKey(id)) return false;
            SongDetails previous = Library.Details.GetValueOrDefault(id) ?? new();
            SongDetails updated = change(previous);
            if (updated == previous) return false;
            bool indexStarted;
            lock (searchIndexGate)
            {
                Library.Details[id] = updated;
                indexStarted = MarkSearchIndexDirty();
            }
            FindSong(id)?.ApplyDetails(updated);
            RefreshDetailsPresentation();
            AnnounceSearchIndexBuild(indexStarted);
            try
            {
                await Store.SaveAsync(Library);
                if (message is not null) Status = message;
                return true;
            }
            catch (Exception ex)
            {
                id = ResolveSongId(id);
                lock (searchIndexGate)
                {
                    Library.Details[id] = rollback(Library.Details.GetValueOrDefault(id) ?? new(), previous, updated);
                    indexStarted = MarkSearchIndexDirty();
                }
                FindSong(id)?.ApplyDetails(Library.Details[id]);
                RefreshDetailsPresentation();
                AnnounceSearchIndexBuild(indexStarted);
                ReportProblem($"歌曲资料保存失败：{ex.Message}");
                return false;
            }
        }
        finally { detailsWrites.Release(); }
    }

    private void RemapDetails(IReadOnlyDictionary<string, string> aliases)
    {
        foreach (var alias in aliases)
        {
            songAliases[alias.Key] = alias.Value;
            if (Library.Details.Remove(alias.Key, out var previous))
                Library.Details[alias.Value] = (Library.Details.GetValueOrDefault(alias.Value) ?? new()).Merge(previous);
        }
    }
}
