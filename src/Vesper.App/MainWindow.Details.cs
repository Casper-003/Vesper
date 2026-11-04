using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Vesper.Core;

namespace Vesper.App;

public sealed partial class MainWindow
{
    private static readonly HttpClient lookupHttp = new() { Timeout = TimeSpan.FromSeconds(15) };
    internal MainViewModel Model => viewModel;
    internal MusicBrainzClient SongLookup { get; } = new(lookupHttp);
    private readonly BilibiliVideoClient videoLookup = new(lookupHttp);
    private readonly LrcLibClient lyricsLookup = new(lookupHttp);
    private readonly SemaphoreSlim automaticLookupSlots = new(2, 2);
    private readonly Dictionary<string, Task> automaticLookups = [];
    private AutomaticSongLookup? automaticLookup;
    private AdditionalSongSources? additionalSources;
    private AdditionalSongSources Sources => additionalSources ??= new(lookupHttp, SongLookup, lyricsLookup);
    internal Task<IReadOnlyList<SongMatch>> SearchSongSourcesAsync(string title, CancellationToken token) => Sources.SearchMetadata(title, token);
    internal Task<SongMetadata> GetSongSourceMetadataAsync(SongMatch match, CancellationToken token) => Sources.GetMetadata(match, token);
    private AutomaticSongLookup AutomaticLookup => automaticLookup ??= new(Sources.SearchMetadata, Sources.GetMetadata,
        (title, token) => Sources.SearchLyrics(title, token), Sources.SearchLyricsWithArtist);
    private readonly HashSet<string> videoLookups = [];
    private readonly Dictionary<string, DateTimeOffset> videoCheckedAt = [];

    private void OnOpenSong(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not LibrarySongItem song) return;
        string key = "歌曲详情:" + song.Song.Id;
        if ((ContentFrame.Content as ShellPage)?.PageKey == key) return;
        NavigateShell(new ShellNavigationRequest(key, this, song.Song.Id));
    }

    internal async Task LoadDetailsAsync(LibrarySongItem song)
    {
        await LoadSongMetadataAsync(song);
        if (closed) return;
        song = viewModel.FindSong(song.Song.Id) ?? song;
        await LoadCoverAsync(song);
        await EnsureAutomaticLookupAsync(song);
        if (closed) return;
        song = viewModel.FindSong(song.Song.Id) ?? song;
        await LoadCoverAsync(song);
        await MatchVideoAsync(song);
    }

    private Task EnsureAutomaticLookupAsync(LibrarySongItem song)
    {
        string id = song.Song.Id;
        if (automaticLookups.TryGetValue(id, out var pending) && !pending.IsCompleted) return pending;
        return automaticLookups[id] = RunAutomaticLookupAsync(song);
    }

    private async Task RunAutomaticLookupAsync(LibrarySongItem song)
    {
        SongLookupInput? input = viewModel.GetLookupInput(song.Song.Id);
        if (closed || viewModel.ReadOnly || input is null
            || !input.ShouldLookup(DateTimeOffset.UtcNow)) return;
        bool entered = false;
        song.LookupPending = true;
        viewModel.BeginMetadataRead();
        try
        {
            await automaticLookupSlots.WaitAsync(metadataCancellation.Token);
            entered = true;
            for (int attempt = 0; attempt < 3 && !closed; attempt++)
            {
                input = viewModel.GetLookupInput(song.Song.Id);
                if (input is null || !input.ShouldLookup(DateTimeOffset.UtcNow)) break;
                var result = await AutomaticLookup.LookupAsync(input, metadataCancellation.Token);
                if (closed) break;
                if (viewModel.GetLookupInput(song.Song.Id)?.Key != input.Key) continue;
                await viewModel.SaveAutomaticLookupAsync(song.Song.Id, input, result);
                break;
            }
        }
        catch (OperationCanceledException) when (metadataCancellation.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!closed && input is not null)
                await viewModel.SaveAutomaticLookupAsync(song.Song.Id, input,
                    new(null, null, LookupOutcome.Unavailable, LookupOutcome.Unavailable));
        }
        finally
        {
            if (entered) automaticLookupSlots.Release();
            song.LookupPending = false;
            if (viewModel.FindSong(song.Song.Id) is { } canonical) canonical.LookupPending = false;
            viewModel.EndMetadataRead();
        }
    }

    private async Task MatchVideoAsync(LibrarySongItem song)
    {
        string? path = song.Song.AvailablePath;
        if (closed || path is null || IsVideoFile(path) || song.HasMatchedVideo || !song.HasArtist || song.DurationSeconds <= 0) return;
        string id = song.Song.Id, title = song.Title, artist = song.ArtistDetail;
        string identity = $"{id}\n{title}\n{artist}\n{song.DurationSeconds}";
        if (videoLookups.Contains(identity) || videoCheckedAt.TryGetValue(identity, out var checkedAt)
            && DateTimeOffset.UtcNow - checkedAt < TimeSpan.FromMinutes(5)) return;
        videoLookups.Add(identity);
        viewModel.BeginMetadataRead();
        try
        {
            var candidates = await videoLookup.SearchAsync(title, artist, metadataCancellation.Token);
            var match = BilibiliVideoClient.ChooseReliable(candidates, title, artist, song.DurationSeconds,
                Path.GetFileNameWithoutExtension(path));
            videoCheckedAt[identity] = DateTimeOffset.UtcNow;
            var current = viewModel.FindSong(id);
            if (!closed && match is not null && current?.Title == title && current.ArtistDetail == artist)
                await viewModel.SaveVideoMatchAsync(id, match, title, artist);
        }
        catch (Exception) { /* 检索失败不打断本地浏览；不要求用户检查候选。 */ }
        finally { videoLookups.Remove(identity); viewModel.EndMetadataRead(); }
    }
}
