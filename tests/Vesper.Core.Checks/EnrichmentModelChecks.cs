using Vesper.App;
using Vesper.Core;

internal static class EnrichmentModelChecks
{
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        await CheckAutomaticCoverageAsync(root, check);
        await CheckStaleResultsAsync(root, check);
        await CheckSourceReplacementAsync(root, check);
        await CheckRetryAsync(root, check);
        await CheckLyricsAndAssetsAsync(root, check);
    }

    private static async Task CheckAutomaticCoverageAsync(string root, Action<bool, string> check)
    {
        string folder = await CreateMediaAsync(root, "enrichment-coverage", "LocalSong", "OnlineSong");
        string store = Path.Combine(root, "enrichment-coverage.json");
        int reads = 0, requests = 0;
        string? artistId, albumId;
        using (var ui = new SerialDispatcher())
        using (var model = new MainViewModel(store))
        {
            var reading = Signal(); var release = Signal();
            async Task<LibraryMetadataRead?> Read(Song song, CancellationToken token)
            {
                if (Interlocked.Increment(ref reads) == 2) reading.TrySetResult();
                await release.Task.WaitAsync(token);
                return new(song.Sources[0], song.Title == "LocalSong"
                    ? new(song.Title, "标签艺人", "标签专辑", Lyrics: "已嵌入歌词") : new(song.Title), "1080p");
            }
            Task<SongLookupResult> Lookup(SongLookupInput input, CancellationToken token)
            {
                Interlocked.Increment(ref requests);
                return Task.FromResult(new SongLookupResult(new("OnlineSong", "联网艺人", "联网专辑"), Lyrics("OnlineSong", "联网艺人"),
                    LookupOutcome.Matched, LookupOutcome.Matched));
            }
            using var controller = new LibraryEnrichmentController(model, Read, Lookup, ui.Run);
            controller.Start();
            await ui.Run(async () => { await model.InitializeAsync(); await model.AddDirectoryAsync(folder); });
            await reading.Task.WaitAsync(TimeSpan.FromSeconds(10));
            check(model.CanManage && model.Results.Count == 2 && controller.State.IsBusy,
                "整库资料读取独立后台运行，未放行读取时初始化已返回且目录仍可管理");
            release.TrySetResult();
            await controller.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(15));
            await ui.Run(model.EnsureSearchIndexAsync);
            var local = model.SearchIndex!.Search("标签艺人", 2000);
            var online = model.SearchIndex.Search("联网专辑", 2001);
            var catalog = model.GetCatalogSnapshot();
            artistId = local.Artists.Items.Single().Id;
            albumId = online.Albums.Items.Single().Id;
            check(reads == 2 && requests == 1 && local.Songs.Items.Count == 1 && online.Songs.Items.Count == 1
                && catalog.GetArtistSongIds(artistId).Count == 1 && catalog.GetAlbumTracks(albumId).Count == 1,
                "不开详情不滚动，空资料曲库自动读取全库标签并补缺资料，艺人专辑卡片关联本地歌曲");
            check(model.Library.Details.Values.All(details => details.LocalReadKey.Length > 0)
                && model.Results.All(song => song.Resolution == "1080p"),
                "后台标签缓存及真实画质回填全库行，无需出现可见容器");
        }
        using (var ui = new SerialDispatcher())
        using (var model = new MainViewModel(store))
        {
            using var controller = new LibraryEnrichmentController(model,
                (_, _) => { Interlocked.Increment(ref reads); throw new InvalidOperationException("unchanged cache must be reused"); },
                (_, _) => { Interlocked.Increment(ref requests); throw new InvalidOperationException("matched data must be reused"); }, ui.Run);
            controller.Start();
            await ui.Run(model.InitializeAsync);
            await controller.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(15));
            await ui.Run(model.EnsureSearchIndexAsync);
            check(reads == 2 && requests == 1 && model.SearchIndex!.Search("标签艺人", 2002).Artists.Items.Single().Id == artistId
                && model.SearchIndex.Search("联网专辑", 2003).Albums.Items.Single().Id == albumId
                && model.Results.All(song => song.Resolution == "1080p"),
                "重启复用未变化文件的标签和联网缓存，不重新遍历媒体或联网，实体身份与画质仍一致");
        }
    }

    private static async Task CheckStaleResultsAsync(string root, Action<bool, string> check)
    {
        foreach (string scenario in new[] { "local-remove", "online-remove", "online-correction" })
        {
            string folder = await CreateMediaAsync(root, "enrichment-" + scenario, "PendingSong");
            using var ui = new SerialDispatcher();
            using var model = new MainViewModel(Path.Combine(root, "enrichment-" + scenario + ".json"));
            await ui.Run(async () => { await model.InitializeAsync(); await model.AddDirectoryAsync(folder); });
            string id = model.Results.Single().Song.Id;
            var entered = Signal(); var release = Signal();
            async Task<LibraryMetadataRead?> Read(Song song, CancellationToken token)
            {
                if (scenario == "local-remove") { entered.TrySetResult(); await release.Task; }
                return new(song.Sources[0], scenario == "local-remove"
                    ? new(song.Title, "过期标签艺人", "过期标签专辑") : new(song.Title));
            }
            async Task<SongLookupResult> Lookup(SongLookupInput input, CancellationToken token)
            {
                if (input.Details.Corrections is not null)
                    return new(null, null, LookupOutcome.Matched, LookupOutcome.NotFound);
                entered.TrySetResult();
                await release.Task; // Deliberately ignore cancellation to exercise the publication guard.
                return new(new("PendingSong", "过期联网艺人", "过期联网专辑"), null, LookupOutcome.Matched, LookupOutcome.Matched);
            }
            using var controller = new LibraryEnrichmentController(model, Read, Lookup, ui.Run);
            controller.Start();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (scenario.EndsWith("remove", StringComparison.Ordinal))
                await ui.Run(() => model.RemoveDirectoryAsync(folder));
            else await ui.Run(async () => { await model.SaveCorrectionsAsync(id, "修正歌名", "保留艺人", ""); });
            release.TrySetResult();
            await controller.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(15));
            await ui.Run(model.EnsureSearchIndexAsync);
            var details = model.Library.Details.GetValueOrDefault(id) ?? new();
            if (scenario.EndsWith("remove", StringComparison.Ordinal))
                check(model.GetEnrichmentSourceKey(id) is null && details.Online is null
                    && details.Local?.Artist != "过期标签艺人" && details.Lookup is null && model.Results.Count == 0,
                    scenario == "local-remove" ? "移除目录后忽略取消的标签读取不能恢复旧来源或发布旧资料"
                        : "移除目录后忽略取消的联网结果不能补回资料或记录失败");
            else check(details.Effective("").Artist == "保留艺人" && details.Effective("").Album == ""
                    && details.Online is null && details.Lookup?.Key == model.GetLookupInput(id)?.Key
                    && model.SearchIndex!.Search("保留艺人", 2010).Artists.Items.Count == 1
                    && model.SearchIndex.Search("过期联网专辑", 2011).Albums.Items.Count == 0,
                    "后台检索期间改正身份并显式清空专辑，晚到结果无写入权且不恢复旧专辑");
        }
    }

    private static async Task CheckSourceReplacementAsync(string root, Action<bool, string> check)
    {
        string folder = await CreateMediaAsync(root, "enrichment-replacement", "ReplacedSong");
        using var ui = new SerialDispatcher();
        using var model = new MainViewModel(Path.Combine(root, "enrichment-replacement.json"));
        await ui.Run(async () => { await model.InitializeAsync(); await model.AddDirectoryAsync(folder); });
        string media = model.Results.Single().Song.Sources[0];
        var entered = Signal(); var release = Signal(); var replacementRead = Signal(); int calls = 0;
        async Task<LibraryMetadataRead?> Read(Song song, CancellationToken token)
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.TrySetResult(); await release.Task;
                return new(media, new(song.Title, "替换前艺人", "替换前专辑", Lyrics: "旧本地歌词"));
            }
            replacementRead.TrySetResult();
            return new(media, new(song.Title, "替换后艺人", "替换后专辑", Lyrics: "新本地歌词"));
        }
        using var controller = new LibraryEnrichmentController(model, Read,
            (_, _) => throw new InvalidOperationException("complete local tags need no network"), ui.Run);
        controller.Start();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await File.WriteAllTextAsync(media, "changed source with a different length and content");
        await ui.Run(model.RefreshAsync);
        await replacementRead.Task.WaitAsync(TimeSpan.FromSeconds(10));
        release.TrySetResult();
        await controller.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(15));
        await ui.Run(model.EnsureSearchIndexAsync);
        check(calls == 2 && model.SearchIndex!.Search("替换后艺人", 2020).Artists.Items.Count == 1
            && model.SearchIndex.Search("替换前艺人", 2021).Artists.Items.Count == 0,
            "同路径媒体替换后按来源签名重新读取，旧文件的晚到标签不能覆盖新文件资料");
    }

    private static async Task CheckRetryAsync(string root, Action<bool, string> check)
    {
        string folder = await CreateMediaAsync(root, "enrichment-retry", "RetrySong");
        var clock = new LibraryEnrichmentChecks.ManualClock();
        using var ui = new SerialDispatcher();
        using var model = new MainViewModel(Path.Combine(root, "enrichment-retry.json"), timeProvider: clock);
        await ui.Run(async () => { await model.InitializeAsync(); await model.AddDirectoryAsync(folder); });
        int reads = 0, requests = 0;
        Task<LibraryMetadataRead?> Read(Song song, CancellationToken token) => Task.FromResult<LibraryMetadataRead?>(
            Interlocked.Increment(ref reads) == 1 ? null : new(song.Sources[0], new(song.Title)));
        Task<SongLookupResult> Lookup(SongLookupInput input, CancellationToken token)
        {
            int attempt = Interlocked.Increment(ref requests);
            return Task.FromResult(attempt == 1 ? new SongLookupResult(null, null, LookupOutcome.Unavailable, LookupOutcome.Unavailable)
                : attempt == 2 ? new(null, null, LookupOutcome.NotFound, LookupOutcome.NotFound)
                : new(new("RetrySong", "恢复艺人", "恢复专辑"), Lyrics("RetrySong", "恢复艺人"), LookupOutcome.Matched, LookupOutcome.Matched));
        }
        using var controller = new LibraryEnrichmentController(model, Read, Lookup, ui.Run, clock);
        controller.Start();
        await controller.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(15));
        check(reads == 1 && requests == 0 && !controller.State.IsBusy && controller.State.Local.WaitingRetryCount == 1,
            "标签读取失败会等待重试，未完成本地读取不提前联网且不持续占用忙碌状态");
        clock.Advance(TimeSpan.FromSeconds(61));
        await controller.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(15));
        check(reads == 2 && requests == 1 && model.Library.Details.Values.Single().Lookup?.Metadata == LookupOutcome.Unavailable,
            "标签重试恢复后进入联网lane，网络失败单独持久记录为可重试状态");
        clock.Advance(TimeSpan.FromSeconds(59));
        await controller.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(15));
        check(requests == 1, "网络失败未超过一分钟冷却不重复请求");
        clock.Advance(TimeSpan.FromSeconds(2));
        await controller.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(15));
        check(requests == 2 && model.Library.Details.Values.Single().Lookup?.Metadata == LookupOutcome.NotFound,
            "超过一分钟后自动重试，查无匹配不冒充服务失败");
        clock.Advance(TimeSpan.FromHours(23) + TimeSpan.FromMinutes(59));
        await controller.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(15));
        check(requests == 2, "查无匹配的一天冷却保留在完整在线候选快照中，不因同步而热循环");
        clock.Advance(TimeSpan.FromSeconds(61));
        await controller.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(15));
        await ui.Run(model.EnsureSearchIndexAsync);
        check(requests == 3 && model.SearchIndex!.Search("恢复专辑", 2030).Albums.Items.Count == 1
            && controller.State.Online.WaitingRetryCount == 0,
            "冷却结束后自动恢复补全并更新专辑搜索，成功结果停止重试");
    }

    private static async Task CheckLyricsAndAssetsAsync(string root, Action<bool, string> check)
    {
        string folder = await CreateMediaAsync(root, "enrichment-assets", "LyricSong", "TaggedSong");
        using var ui = new SerialDispatcher();
        using var model = new MainViewModel(Path.Combine(root, "enrichment-assets.json"));
        await ui.Run(async () => { await model.InitializeAsync(); await model.AddDirectoryAsync(folder); });
        int requests = 0, assets = 0, active = 0, maxActive = 0;
        var entered = Signal(); var release = Signal();
        Task<LibraryMetadataRead?> Read(Song song, CancellationToken token) => Task.FromResult<LibraryMetadataRead?>(
            new(song.Sources[0], new(song.Title, "完整标签艺人", "完整标签专辑",
                Lyrics: song.Title == "TaggedSong" ? "已有标签歌词" : "")));
        Task<SongLookupResult> Lookup(SongLookupInput input, CancellationToken token)
        {
            Interlocked.Increment(ref requests);
            var effective = input.Details.Effective("");
            return Task.FromResult(new SongLookupResult(new(effective.Title, effective.Artist, effective.Album,
                CoverUrl: "https://coverartarchive.org/release/example/front-500"),
                effective.Lyrics.Length == 0 ? Lyrics(effective.Title, effective.Artist) : null,
                LookupOutcome.Matched, LookupOutcome.Matched));
        }
        async Task<LibraryEnrichmentOutcome> Assets(LibraryEnrichmentTicket ticket, string sourceKey)
        {
            int current = Interlocked.Increment(ref active);
            maxActive = Math.Max(maxActive, current);
            Interlocked.Increment(ref assets);
            await ui.Run(() =>
            {
                check(model.GetEnrichmentSourceKey(ticket.SongId) == sourceKey
                    && model.GetLookupInput(ticket.SongId)?.Details.Online?.CoverUrl.Length > 0,
                    "后台图片回调在已发布的身份与封面资料之后执行，并保留当前来源票据");
                return Task.CompletedTask;
            });
            entered.TrySetResult();
            await release.Task.WaitAsync(ticket.CancellationToken);
            Interlocked.Decrement(ref active);
            return LibraryEnrichmentOutcome.Completed;
        }
        using (var controller = new LibraryEnrichmentController(model, Read, Lookup, ui.Run, synchronizeAssets: Assets))
        {
            controller.Start();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            check(model.CanManage && controller.State.Online.ActiveCount == 1 && requests is >= 1 and <= 2 && assets == 1,
                "完整本地标签同样自动发现图片身份，联网与图片共用单路队列，前台管理保持可用");
            release.TrySetResult();
            await controller.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(15));
            check(requests == 2 && assets == 2 && maxActive == 1
                && model.Library.Details.Values.All(details => details.Effective("").Lyrics.Length > 0),
                "仅缺歌词的歌曲不需打开详情即可补全，完整标签歌曲也进入图片采集且不并发堆积");
        }

        // Asset work has its own retry state; a metadata/lyrics negative-cache interval
        // must not prevent a known cover from being downloaded.
        int coolingRequests = 0, coolingAssets = 0;
        await ui.Run(() =>
        {
            foreach (var song in model.Library.Songs)
            {
                var input = model.GetLookupInput(song.Id)!;
                model.Library.Details[song.Id] = input.Details with
                {
                    OnlineLyrics = null,
                    Lookup = new(input.Key, DateTimeOffset.UtcNow, LookupOutcome.NotFound, LookupOutcome.NotFound)
                };
            }
            return Task.CompletedTask;
        });
        using var cooling = new LibraryEnrichmentController(model,
            (_, _) => throw new InvalidOperationException("unchanged media must be reused"),
            (_, _) => { Interlocked.Increment(ref coolingRequests); throw new InvalidOperationException("lookup still cooling down"); },
            ui.Run, synchronizeAssets: (_, _) => { Interlocked.Increment(ref coolingAssets); return Task.FromResult(LibraryEnrichmentOutcome.Completed); });
        cooling.Start();
        await cooling.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        check(coolingRequests == 0 && coolingAssets == 2 && cooling.State.Online.WaitingRetryCount == 1,
            "歌词查无匹配的冷却不会阻塞已知图片同步，仍缺歌词的曲目保留延后重试");
    }

    private static SongLyrics Lyrics(string title, string artist) => new("已同步歌词", "", "", false,
        "https://lrclib.net/api/get/1", title, artist, 180, true);

    private static async Task<string> CreateMediaAsync(string root, string name, params string[] titles)
    {
        string folder = Path.Combine(root, name); Directory.CreateDirectory(folder);
        for (int index = 0; index < titles.Length; index++)
            await File.WriteAllTextAsync(Path.Combine(folder, titles[index] + ".mp4"), name + new string('x', index + 1));
        return folder;
    }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Models are UI-owned. A serial async dispatcher exercises the same boundary without creating a window.
    private sealed class SerialDispatcher : IDisposable
    {
        private readonly SemaphoreSlim gate = new(1, 1);
        public async Task Run(Func<Task> action)
        {
            await gate.WaitAsync();
            try { await action(); }
            finally { gate.Release(); }
        }
        public void Dispose() => gate.Dispose();
    }
}
