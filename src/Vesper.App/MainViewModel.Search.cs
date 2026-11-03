using Vesper.Core;

namespace Vesper.App;

public delegate Task<LocalSearchIndex> BuildSearchIndex(IReadOnlyList<SearchDocument> documents,
    long revision, bool hideUnavailable, CancellationToken token);

public sealed partial class MainViewModel
{
    private static readonly TimeSpan SearchIndexMergeDelay = TimeSpan.FromMilliseconds(25);
    private readonly object searchIndexGate = new();
    private readonly CancellationTokenSource searchIndexLifetime = new();
    private readonly BuildSearchIndex searchIndexBuilder;
    private HashSet<string> searchAvailableSources = new(StringComparer.OrdinalIgnoreCase);
    private LocalSearchIndex? searchIndex;
    private Task? searchBuildTask;
    private long requestedSearchIndexRevision;
    private bool searchIndexBuilding;
    private string searchIndexProblem = "";

    /// <summary>Published immutable snapshot. A rebuild leaves this snapshot usable.</summary>
    public LocalSearchIndex? SearchIndex { get { lock (searchIndexGate) return searchIndex; } }
    public long IndexRevision { get { lock (searchIndexGate) return searchIndex?.Revision ?? 0; } }
    public long RequestedSearchIndexRevision { get { lock (searchIndexGate) return requestedSearchIndexRevision; } }
    public bool IsSearchReady { get { lock (searchIndexGate) return searchIndex is not null; } }
    public bool IsSearchIndexBuilding { get { lock (searchIndexGate) return searchIndexBuilding; } }
    public string SearchIndexProblem { get { lock (searchIndexGate) return searchIndexProblem; } }

    // Raised outside the state lock, possibly on a worker thread. UI consumers dispatch
    // to their UI thread and read the current properties rather than trusting an old event.
    public event EventHandler? SearchIndexChanged;

    public Task EnsureSearchIndexAsync()
    {
        bool started = false;
        Task pending;
        lock (searchIndexGate)
        {
            if (disposed || !initialized || ReadOnly) return Task.CompletedTask;
            if (!searchIndexBuilding && (searchIndex is null || searchIndexProblem.Length > 0))
            {
                requestedSearchIndexRevision++;
                StartSearchIndexBuild();
                started = true;
            }
            pending = searchBuildTask ?? Task.CompletedTask;
        }
        if (started) SearchIndexChanged?.Invoke(this, EventArgs.Empty);
        return pending;
    }

    private void InvalidateSearchIndex()
    {
        bool started;
        lock (searchIndexGate) started = MarkSearchIndexDirty();
        if (started) SearchIndexChanged?.Invoke(this, EventArgs.Empty);
    }

    // Call while holding searchIndexGate, in the same critical section as a library
    // mutation, so an older in-flight build loses publication rights immediately.
    private bool MarkSearchIndexDirty()
    {
        if (disposed || !initialized || ReadOnly) return false;
        requestedSearchIndexRevision++;
        if (searchIndexBuilding) return false;
        StartSearchIndexBuild();
        return true;
    }

    private void AnnounceSearchIndexBuild(bool started)
    { if (started) SearchIndexChanged?.Invoke(this, EventArgs.Empty); }

    // Caller holds searchIndexGate. Only one runner exists; mutations during a build
    // are merged into its next pass, and only the latest revision can be published.
    private void StartSearchIndexBuild()
    {
        searchIndexBuilding = true;
        searchIndexProblem = "";
        searchBuildTask = Task.Run(RebuildSearchIndexAsync);
    }

    private async Task RebuildSearchIndexAsync()
    {
        while (true)
        {
            long revision = 0;
            LocalSearchIndex? built = null;
            string failure = "";
            try
            {
                // Batch row metadata arrivals without copying the entire library on
                // every callback. This is independent of the user's input debounce.
                await Task.Delay(SearchIndexMergeDelay, searchIndexLifetime.Token).ConfigureAwait(false);
                bool hide;
                IReadOnlyList<SearchDocument> documents;
                lock (searchIndexGate)
                {
                    if (disposed || ReadOnly) { searchIndexBuilding = false; return; }
                    revision = requestedSearchIndexRevision;
                    hide = hideUnavailable;
                    // Only immutable values leave this lock. Never enumerate live library
                    // dictionaries during the expensive normalization / pinyin build.
                    documents = CaptureSearchDocuments();
                }
                built = await searchIndexBuilder(documents, revision, hide, searchIndexLifetime.Token)
                    .ConfigureAwait(false);
                if (built is null || built.Revision != revision)
                    throw new InvalidOperationException("索引构建返回了不一致的修订号。");
            }
            catch (OperationCanceledException) when (searchIndexLifetime.IsCancellationRequested)
            {
                lock (searchIndexGate) searchIndexBuilding = false;
                return;
            }
            catch (Exception ex) { built = null; failure = $"搜索索引准备失败，可重试：{ex.Message}"; }

            lock (searchIndexGate)
            {
                if (disposed || ReadOnly) { searchIndexBuilding = false; return; }
                // Even a builder that ignores cancellation cannot replace newer data.
                if (revision != requestedSearchIndexRevision) continue;
                if (built is not null) searchIndex = built;
                searchIndexProblem = failure;
                searchIndexBuilding = false;
            }
            SearchIndexChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
    }

    private IReadOnlyList<SearchDocument> CaptureSearchDocuments()
    {
        ReconcileCatalog();
        var documents = new SearchDocument[Library.Songs.Count];
        for (int i = 0; i < documents.Length; i++)
        {
            Song song = Library.Songs[i];
            SongMetadata effective = (Library.Details.GetValueOrDefault(song.Id) ?? new()).Effective(song.Title);
            var resources = song.Sources.Select((path, order) => new SearchResource(path, song.Id,
                path, searchAvailableSources.Contains(path), SourceOrder: order)).ToArray();
            // Current library data has no verified version/group/quality/added-at
            // identities. Core maps each SongId to one stable single-member group.
            // In particular, do not parse filenames into fields that the UI does not
            // display, and never refill a correction's explicitly cleared artist/album.
            var album = Library.Catalog.GetSongAlbum(song.Id);
            documents[i] = new(song.Id, effective.Title, effective.Artist, effective.Album,
                availability.GetValueOrDefault(song.Id))
            {
                Resources = Array.AsReadOnly(resources), AlbumId = album?.Id,
                AlbumArtist = album?.Artist ?? "",
                Artists = Library.Catalog.GetSongArtists(song.Id).Select(artist => new SearchArtistCredit(artist.Name, artist.Id)).ToArray()
            };
        }
        return Array.AsReadOnly(documents);
    }

    private void FailSearchIndexInitialization(string message)
    {
        lock (searchIndexGate)
        {
            requestedSearchIndexRevision++;
            searchIndexProblem = message;
        }
        SearchIndexChanged?.Invoke(this, EventArgs.Empty);
    }

    private void DisposeSearchIndex()
    {
        lock (searchIndexGate) disposed = true;
        searchIndexLifetime.Cancel();
    }
}
