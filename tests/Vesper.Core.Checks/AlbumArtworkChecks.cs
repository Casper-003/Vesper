using Vesper.Core;

internal static class AlbumArtworkChecks
{
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        const string coverA = "https://p1.music.126.net/persisted-album-a.jpg";
        const string coverB = "https://cdn-images.dzcdn.net/another-track-release-b.jpg";
        var album = new CatalogAlbum("album:stable", "Album", "Singer") { CoverUrl = coverA };
        AlbumArtworkCandidate[] candidates = [new("second", "local-b", true), new("first", "local-a", true)];
        var selected = AlbumArtwork.Choose(album, candidates);
        check(selected?.CoverUrl == coverA && selected.SongId.Length == 0,
            "专辑封面取持久专辑图片，不从已加载歌曲或其他发行版本中重新选择");
        check(AlbumArtwork.Choose(album, candidates.Reverse()) == selected
            && AlbumArtwork.Choose(album, []) == selected,
            "歌曲加载顺序、排序及当前筛选变化不替换已有专辑封面");
        check(AlbumArtwork.Choose(album with { Name = "Renamed Album", Artist = "Renamed Singer" }, candidates) == selected,
            "专辑或艺人显示名称变化不使同一图片身份失效");
        check(AlbumArtwork.Choose(album with { CoverUrl = coverB }, candidates)?.Key != selected!.Key,
            "明确替换专辑权威图片时才创建新的图片身份");

        var localAlbum = album with { CoverUrl = "" };
        var local = AlbumArtwork.Choose(localAlbum, candidates);
        check(local?.SongId == "first" && AlbumArtwork.Choose(localAlbum, candidates.Reverse()) == local,
            "无联网专辑图时固定按SongId选本地内嵌来源，不按标题或完成先后跳图");
        check(AlbumArtwork.Choose(localAlbum, [new("first", "changed-file", true), candidates[0]])?.Key != local!.Key,
            "本地来源文件身份改变会使旧排队图片失效");
        check(AlbumArtwork.Choose(localAlbum, [new("first", "video", false), candidates[0]])?.SongId == "second"
            && AlbumArtwork.Choose(localAlbum, [new("first", "video", false)]) is null,
            "本地回退只选择可读取内嵌图的音频来源，不用视频帧冒充专辑图");

        var state = new LibraryState
        {
            Songs = [new("a", "First", []), new("b", "Second", [])],
            Details = new()
            {
                ["a"] = new(Online: new("First", "Singer", "Album", CoverUrl: coverA)),
                ["b"] = new(Online: new("Second", "Singer", "Album", CoverUrl: coverB))
            }
        };
        state.Catalog.Reconcile(state);
        var original = state.Catalog.GetSongAlbum("a")!;
        state.Songs.Reverse();
        state.Details["a"] = state.Details["a"] with
        { Online = state.Details["a"].Online! with { CoverUrl = "https://p1.music.126.net/later-result-c.jpg" } };
        state.Catalog.Reconcile(state);
        check(state.Catalog.GetSongAlbum("a") is { } retained && retained.Id == original.Id && retained.CoverUrl == coverA,
            "其他歌曲或后到资料带来不同封面时，已接纳的专辑图片保持不变");
        var store = new LibraryStore(Path.Combine(root, "stable-album-artwork.json"));
        await store.SaveAsync(state);
        var restored = await store.LoadAsync();
        restored.Catalog.Reconcile(restored);
        var restoredAlbum = restored.Catalog.GetSongAlbum("a")!;
        check(restoredAlbum.Id == original.Id && restoredAlbum.CoverUrl == coverA
            && AlbumArtwork.Choose(restoredAlbum, candidates)?.CoverUrl == coverA,
            "重启保留同一专辑的图片选择，不因扫描顺序或较新曲目结果换图");
    }
}
