using System.Text.Json;
using Vesper.App;
using Vesper.Core;

internal static class CatalogChecks
{
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        var legacy = JsonSerializer.Deserialize<LibraryState>("""{"Songs":[{"Id":"legacy","Title":"旧歌曲","Sources":[]}],"Details":{"legacy":{"Local":{"Title":"旧歌曲","Artist":"旧艺人","Album":"旧专辑"}}}}""")!;
        legacy.Catalog.Reconcile(legacy);
        check(legacy.Catalog.GetSongArtists("legacy").Count == 1 && legacy.Catalog.GetSongAlbum("legacy") is not null
            && legacy.Catalog.GetAlbumTracks(legacy.Catalog.GetSongAlbum("legacy")!.Id).Single().TrackNumber is null,
            "旧库缺少catalog和曲序字段仍可建立本地实体，不补造曲号");

        var state = new LibraryState
        {
            Songs = [new("two", "第二首", []), new("seven", "第七首", []), new("unknown-b", "未知乙", []), new("unknown-a", "未知甲", [])],
            Details = new()
            {
                ["two"] = new(Local: new("第二首", "艺人甲", "本地专辑", Year: "2020") { TrackNumber = 2 }),
                ["seven"] = new(Local: new("第七首", "艺人甲", "本地专辑", Year: "2020") { TrackNumber = 7 }),
                ["unknown-b"] = new(Local: new("未知乙", "艺人甲", "本地专辑", Year: "2020")),
                ["unknown-a"] = new(Local: new("未知甲", "艺人甲", "本地专辑", Year: "2020"))
            },
            Favorites = ["two"], PendingQueue = [new(Guid.NewGuid(), "two", "第二首")]
        };
        state.Catalog.Reconcile(state);
        string artistId = state.Catalog.GetSongArtists("two").Single().Id;
        string albumId = state.Catalog.GetSongAlbum("two")!.Id;
        var tracks = state.Catalog.GetAlbumTracks(albumId);
        check(tracks.Select(track => track.TrackNumber).SequenceEqual(new int?[] { 2, 7, null, null })
            && tracks.Take(2).Select(track => track.SongId).SequenceEqual(new[] { "two", "seven" }),
            "专辑保留真实02和07音轨号，未知音轨不重编号且排在有编号歌曲之后");
        string[] unknownOrder = tracks.Skip(2).Select(track => track.SongId).ToArray();
        state.Songs.Reverse(); state.Catalog.Reconcile(state);
        check(state.Catalog.GetAlbumTracks(albumId).Skip(2).Select(track => track.SongId).SequenceEqual(unknownOrder),
            "未知曲序使用稳定歌名顺序，扫描顺序变化不使其跳动");

        var store = new LibraryStore(Path.Combine(root, "catalog.json"));
        await store.SaveAsync(state);
        var restored = await store.LoadAsync(); restored.Catalog.Reconcile(restored);
        check(restored.Catalog.GetSongArtists("two").Single().Id == artistId && restored.Catalog.GetSongAlbum("two")?.Id == albumId
            && restored.Favorites.Contains("two") && restored.PendingQueue.Single().SongId == "two",
            "本地艺人专辑独立ID跨保存和启动稳定，SongId收藏队列保持原关联");
        var beforeRename = state.Details["two"];
        state.Details["two"] = beforeRename with { Corrections = new("新的歌名", "艺人甲", "本地专辑", DateTimeOffset.UnixEpoch) };
        state.Catalog.Reconcile(state);
        check(state.Catalog.GetSongArtists("two").Single().Id == artistId && state.Catalog.GetSongAlbum("two")?.Id == albumId,
            "仅修正歌名不改变艺人专辑身份");
        state.Catalog.RenameArtist(artistId, "艺人甲新名称"); state.Catalog.RenameAlbum(albumId, "专辑新名称");
        state.Catalog.Reconcile(state);
        check(state.Catalog.GetSongArtists("two").Single() is { Name: "艺人甲新名称" } renamed && renamed.Id == artistId
            && state.Catalog.GetSongAlbum("two") is { Name: "专辑新名称" } renamedAlbum && renamedAlbum.Id == albumId,
            "实体改名保留独立ID，旧资料名称作为别名继续关联");

        var corrected = new SongDetails(Local: new("曲目", "原艺人", "原发行版")
        {
            AlbumArtist = "原艺人", DiscNumber = 2, TrackNumber = 7,
            ExternalIds = [new("musicbrainz", "release", "original-release"), new("musicbrainz", "artist", "original-artist")]
        }, Corrections: new("曲目", "新艺人", "新专辑", DateTimeOffset.UnixEpoch));
        var effective = corrected.Effective("曲目");
        check(effective.Album == "新专辑" && effective.Artist == "新艺人" && effective.AlbumArtist == ""
            && effective.DiscNumber is null && effective.TrackNumber is null && effective.ExternalIds.Count == 0,
            "手动改正专辑或艺人归属不会继承原发行版曲序及外部身份");
        state.Details["two"] = corrected;
        state.Catalog.Reconcile(state);
        check(state.Catalog.GetSongAlbum("two")?.Id != albumId && state.Catalog.GetSongArtists("seven").Single().Id == artistId
            && !state.Catalog.GetAlbumTracks(albumId).Any(track => track.SongId == "two"),
            "单曲改归属只迁移本曲主关系，不改名其他歌曲共用的实体");
        state.Details["two"] = beforeRename; state.Catalog.Reconcile(state);
        check(state.Catalog.GetSongAlbum("two")?.Id == albumId && state.Catalog.GetSongArtists("two").Single().Id == artistId,
            "保存失败后恢复原资料可以找回原稳定实体，不生成替代身份");

        var separated = new LibraryState();
        Add("artist-a", new("甲曲", "甲艺人", "同名专辑", Year: "2020"));
        Add("artist-b", new("乙曲", "乙艺人", "同名专辑", Year: "2020"));
        Add("year-b", new("甲曲再版", "甲艺人", "同名专辑", Year: "2025"));
        Add("unknown-one", new("匿名甲", Album: "同名专辑"));
        Add("unknown-two", new("匿名乙", Album: "同名专辑"));
        separated.Catalog.Reconcile(separated);
        check(separated.Songs.Select(song => separated.Catalog.GetSongAlbum(song.Id)!.Id).Distinct().Count() == 5,
            "同名不同艺人、不同年份和艺人未知的专辑保守分开");
        string provisionalArtist = separated.Catalog.GetSongArtists("artist-a").Single().Id;
        string provisionalAlbum = separated.Catalog.GetSongAlbum("artist-a")!.Id;
        separated.Details["artist-a"] = new(Local: new("甲曲", "甲艺人", "同名专辑", Year: "2020")
        {
            ExternalIds = [new("musicbrainz", "artist", "artist-a-id"), new("musicbrainz", "release", "release-a-id")]
        });
        separated.Catalog.Reconcile(separated);
        check(separated.Catalog.GetSongArtists("artist-a").Single().Id == provisionalArtist
            && separated.Catalog.GetSongAlbum("artist-a")?.Id == provisionalAlbum
            && separated.Catalog.GetSongAlbum("artist-a")!.ExternalIds.Any(id => id.Value == "release-a-id"),
            "为已关联本地实体补可信外部身份保留原artist和album ID");
        Add("known-edition-b", new("另一发行版", "甲艺人", "同名专辑", Year: "2020")
        {
            ExternalIds = [new("musicbrainz", "artist", "artist-a-id"), new("musicbrainz", "release", "release-b-id")]
        });
        Add("known-artist-b", new("另一同名艺人", "甲艺人", "同名专辑", Year: "2020")
        {
            ExternalIds = [new("musicbrainz", "artist", "different-artist-id"), new("musicbrainz", "release", "release-c-id")]
        });
        separated.Catalog.Reconcile(separated);
        check(separated.Catalog.GetSongAlbum("known-edition-b")?.Id != provisionalAlbum
            && separated.Catalog.GetSongArtists("known-artist-b").Single().Id != provisionalArtist
            && separated.Catalog.GetSongAlbum("artist-a")?.Id == provisionalAlbum,
            "外部身份不同的同名艺人与发行版不误合，已有身份升级不受影响");
        string anonymousAlbum = separated.Catalog.GetSongAlbum("unknown-one")!.Id;
        separated.Details["unknown-one"] = new(Local: new("匿名甲", Album: "同名专辑"),
            Corrections: new("匿名甲", "", "", DateTimeOffset.UnixEpoch));
        separated.Catalog.Reconcile(separated);
        separated.Details["unknown-one"] = new(Local: new("匿名甲", Album: "同名专辑"));
        separated.Catalog.Reconcile(separated);
        check(separated.Catalog.GetSongAlbum("unknown-one")?.Id == anonymousAlbum,
            "匿名专辑清空资料后撤回仍找到本曲原实体，不混同其他匿名同名专辑");
        separated.Details["unknown-one"] = new(Local: new("匿名甲", "补齐艺人", "同名专辑"));
        separated.Catalog.Reconcile(separated);
        check(separated.Catalog.GetSongAlbum("unknown-one")?.Id == anonymousAlbum
            && separated.Catalog.GetSongAlbum("unknown-one")?.Artist == "补齐艺人",
            "从未知艺人补齐可信归属时补绑定原专辑实体，保持其本地ID");

        var enrichedCredits = new SongDetails(Local: new("署名曲", "艺人甲", "") { ArtistCredits = [new("艺人甲")] },
            Online: new("署名曲", "艺人甲", "")
            { ArtistCredits = [new("艺人甲", ExternalIds: [new("musicbrainz", "artist", "matched-artist")])] }).Effective("署名曲");
        check(enrichedCredits.ArtistCredits.Single().ExternalIds!.Any(id => id.Value == "matched-artist"),
            "保留本地结构化署名时可补入同名同角色已确认线上身份，不因本地优先丢失联网映射");
        var trustedOnlineRelease = new SongDetails(Local: new("发行曲", "发行艺人", "发行专辑") { TrackNumber = 7 },
            Online: new("发行曲", "发行艺人", "发行专辑")
            { ExternalIds = [new("musicbrainz", "release", "trusted-edition")] }).Effective("发行曲");
        check(trustedOnlineRelease.TrackNumber == 7 && trustedOnlineRelease.ExternalIds.Any(id => id.Value == "trusted-edition"),
            "真实本地曲号与另行确认的线上发行身份可同时保留，描述性候选仍不升级为发行归属");

        var multi = new LibraryState
        {
            Songs = [new("disc-one", "碟一", []), new("disc-two", "碟二", []), new("collaboration", "合作曲", [])],
            Details = new()
            {
                ["disc-one"] = new(Local: new("碟一", "艺人", "双碟") { DiscNumber = 1, TrackNumber = 7 }),
                ["disc-two"] = new(Local: new("碟二", "艺人", "双碟") { DiscNumber = 2, TrackNumber = 2 }),
                ["collaboration"] = new(Local: new("合作曲", "艺人 / 嘉宾", "合辑")
                {
                    ArtistCredits = [new("艺人"), new("嘉宾"), new("作曲家", "composer")]
                })
            }
        };
        multi.Catalog.Reconcile(multi);
        string doubleAlbum = multi.Catalog.GetSongAlbum("disc-one")!.Id;
        check(multi.Catalog.GetAlbumTracks(doubleAlbum).Select(track => (track.DiscNumber, track.TrackNumber))
            .SequenceEqual(new[] { ((int?)1, (int?)7), ((int?)2, (int?)2) }),
            "多碟专辑按真实碟号再按曲号排列，不因第二碟曲号较小而提前");
        check(multi.Catalog.GetSongArtists("collaboration").Select(artist => artist.Name).SequenceEqual(new[] { "艺人", "嘉宾" })
            && multi.Catalog.GetSongCredits("collaboration").Count == 3,
            "结构化合作演唱分别关联艺人，作曲角色保留但不混入演唱搜索实体");
        string composerId = multi.Catalog.Artists.Single(artist => artist.Name == "作曲家").Id;
        check(multi.Catalog.GetArtistSongIds(composerId).Count == 0 && multi.Catalog.GetArtistAlbums(composerId).Count == 0,
            "演唱艺人页的歌曲及专辑查询不会把单纯作曲署名当作演唱关联");
        multi.Catalog.AddAlbumTrack(doubleAlbum, "collaboration", 2, 7);
        multi.Catalog.Reconcile(multi);
        check(multi.Catalog.GetAlbumTracks(doubleAlbum).Any(track => track.SongId == "collaboration" && track.TrackNumber == 7)
            && multi.Catalog.GetSongAlbum("collaboration")?.Id != doubleAlbum,
            "同一SongId可属于多个专辑成员，额外成员关系不覆盖原专辑归属");

        var snapshot = separated.Catalog.Snapshot();
        separated.Catalog.RenameAlbum(provisionalAlbum, "后来改名");
        separated.Catalog.Artists.Clear(); separated.Catalog.SongAssociations.Clear();
        check(snapshot.Albums.First(album => album.Id == provisionalAlbum).Name == "同名专辑"
            && snapshot.GetSongArtists("artist-a").Single().Id == provisionalArtist,
            "保存快照独立复制catalog列表和关联字典，不受后续界面修改影响");

        var saveFirst = new LibraryState { Songs = [new("save-first", "保存先于索引", [])],
            Details = new() { ["save-first"] = new(Local: new("保存先于索引", "保存艺人", "保存专辑")) } };
        var saveFirstStore = new LibraryStore(Path.Combine(root, "catalog-save-first.json"));
        await saveFirstStore.SaveAsync(saveFirst);
        string savedId = saveFirst.Catalog.GetSongAlbum("save-first")!.Id;
        check((await saveFirstStore.LoadAsync()).Catalog.GetSongAlbum("save-first")?.Id == savedId,
            "首次保存先协调并持久化实体身份，不依赖异步搜索索引抢先建立");
        using var model = new MainViewModel(Path.Combine(root, "catalog-save-first.json"));
        await model.InitializeAsync(); model.HideUnavailable = false;
        await model.EnsureSearchIndexAsync();
        var catalogForNavigation = model.GetCatalogSnapshot();
        var searchHit = model.SearchIndex!.Search("保存专辑", 201).Albums.Items.Single();
        var artistHit = model.SearchIndex.Search("保存艺人", 202).Artists.Items.Single();
        check(searchHit.Id == savedId && catalogForNavigation.Albums.Any(album => album.Id == searchHit.Id)
            && catalogForNavigation.Artists.Any(artist => artist.Id == artistHit.Id)
            && catalogForNavigation.GetAlbumTracks(searchHit.Id).Single().SongId == "save-first",
            "真实ViewModel搜索卡片与页面catalog快照消费同一持久身份，可导航找到本地歌曲");

        void Add(string id, SongMetadata metadata)
        { separated.Songs.Add(new(id, metadata.Title, [])); separated.Details[id] = new(Local: metadata); }
    }
}
