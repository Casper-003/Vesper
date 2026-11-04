using System.Text.Json;

namespace Vesper.Core;

public sealed record Song(string Id, string Title, List<string> Sources)
{
    public List<string> KnownSources { get; init; } = [];
    public string Artist => "未知艺人";
    public string? AvailablePath => Sources.FirstOrDefault(File.Exists);
    public bool IsAvailable => AvailablePath is not null;
}

public sealed class LibraryState
{
    public List<string> Directories { get; set; } = [];
    public List<Song> Songs { get; set; } = [];
    public HashSet<string> Favorites { get; set; } = [];
    public Dictionary<string, DateTimeOffset> FavoriteAddedAt { get; set; } = [];
    public Dictionary<string, SongDetails> Details { get; set; } = [];
    public LibraryCatalog Catalog { get; set; } = new();
    public Dictionary<string, CachedArtistProfile> ArtistProfiles { get; set; } = [];
    public List<QueueEntry> PendingQueue { get; set; } = [];
    public QueueEntry? CurrentQueueSong { get; set; }
    public Dictionary<string, FileScanCache> ScanCache { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class LibraryStore(string path)
{
    private readonly SemaphoreSlim saveLock = new(1, 1);
    public async Task<LibraryState> LoadAsync()
    {
        if (!File.Exists(path)) return new();
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<LibraryState>(stream)
            ?? throw new InvalidDataException("曲库记录为空，请先备份并检查本机曲库文件。");
    }

    public async Task SaveAsync(LibraryState state, bool reconcileCatalog = true)
    {
        if (reconcileCatalog) state.Catalog.Reconcile(state);
        var snapshot = new LibraryState
        {
            Directories = [.. state.Directories], Songs = [.. state.Songs], Favorites = [.. state.Favorites],
            FavoriteAddedAt = new(state.FavoriteAddedAt),
            Details = new(state.Details),
            Catalog = state.Catalog.Snapshot(),
            ArtistProfiles = new(state.ArtistProfiles),
            PendingQueue = [.. state.PendingQueue], CurrentQueueSong = state.CurrentQueueSong,
            ScanCache = new(state.ScanCache, StringComparer.OrdinalIgnoreCase)
        };
        await saveLock.WaitAsync();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            string temporary = path + ".tmp";
            await Task.Run(async () =>
            {
                await using var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                    16384, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await JsonSerializer.SerializeAsync(output, snapshot, new JsonSerializerOptions { WriteIndented = true });
            });
            File.Move(temporary, path, overwrite: true);
        }
        finally { saveLock.Release(); }
    }
}

public sealed record FileScanCache(long Length, long ModifiedUtcTicks, string SongId, string? ContentHash);
public sealed record ScanProgress(string Phase, int Completed, int Total);
public sealed record ScanStatistics(int Files, int HashedFiles, long HashedBytes, TimeSpan Elapsed);
public sealed record ScanResult(List<Song> Songs, List<string> Warnings,
    Dictionary<string, FileScanCache> Cache, Dictionary<string, string> Aliases, ScanStatistics Statistics);
