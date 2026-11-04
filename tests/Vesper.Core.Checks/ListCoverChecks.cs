using Vesper.App;
using Vesper.Core;

internal static class ListCoverChecks
{
    public static void Run(Action<bool, string> check)
    {
        var item = new LibrarySongItem(new("list-art", "Song", ["C:\\Music\\song.mp3"])) { MetadataLoaded = true };
        var events = new List<string?>();
        item.PropertyChanged += (_, args) => events.Add(args.PropertyName);
        var thumbnail = new object();
        item.Thumbnail = thumbnail;
        check(item.MetadataLoaded && item.Cover is null && ReferenceEquals(item.ListCover, thumbnail)
            && item.HasListCover && !item.HasNoListCover
            && events.Contains(nameof(LibrarySongItem.ListCover)) && events.Contains(nameof(LibrarySongItem.HasListCover)),
            "列表可独立显示缩略封面并通知图片与占位，无需加载详情大图或重新读取已有标签");

        var fullSize = new object();
        events.Clear(); item.Cover = fullSize;
        check(ReferenceEquals(item.ListCover, thumbnail) && !events.Contains(nameof(LibrarySongItem.ListCover)),
            "详情大图加载不替换已有列表缩略图，也不重复刷新列表图像");
        events.Clear(); item.Thumbnail = null;
        check(ReferenceEquals(item.ListCover, fullSize) && item.HasListCover
            && events.Contains(nameof(LibrarySongItem.ListCover)) && !events.Contains(nameof(LibrarySongItem.HasNoListCover)),
            "缩略图被回收时列表复用已有详情封面，不闪回空占位");
        events.Clear(); item.Cover = null;
        check(item.ListCover is null && item.HasNoListCover && !item.HasListCover
            && events.Contains(nameof(LibrarySongItem.HasNoListCover)),
            "最后一个封面被清理时列表恢复占位并发出可见性通知");

        var local = new SongDetails(Local: new("Song", "Singer", "Album")) { LocalReadKey = "local-v1" };
        item.ApplyDetails(local); item.Thumbnail = thumbnail; item.Cover = fullSize;
        string localIdentity = item.ArtworkIdentity;
        var online = local with { Online = new("Song", "Singer", "Album", CoverUrl: "https://p1.music.126.net/album-a.jpg") };
        events.Clear(); item.ApplyDetails(online);
        check(item.ArtworkIdentity != localIdentity && item.Cover is null && item.Thumbnail is null
            && events.Contains(nameof(LibrarySongItem.ArtworkIdentity)),
            "后台封面URL到达已显示且标签已读取的歌曲时，旧图失效并通知可见行重新请求");

        item.Thumbnail = thumbnail; item.Cover = fullSize;
        string onlineIdentity = item.ArtworkIdentity;
        var lyrics = online with { OnlineLyrics = new("New lyrics", "", "", false, "https://lrclib.net/api/get/1", "Song", "Singer", 180, true),
            Lookup = new("lookup", DateTimeOffset.UtcNow, LookupOutcome.Matched, LookupOutcome.Matched) };
        events.Clear(); item.ApplyDetails(lyrics);
        check(item.ArtworkIdentity == onlineIdentity && ReferenceEquals(item.Cover, fullSize)
            && ReferenceEquals(item.Thumbnail, thumbnail) && !events.Contains(nameof(LibrarySongItem.ArtworkIdentity)),
            "歌词和请求状态更新保留已显示的列表与详情封面，不引起重复加载");

        var correction = lyrics with { Corrections = new("Song", "Singer", "", DateTimeOffset.UtcNow) };
        events.Clear(); item.ApplyDetails(correction);
        check(item.CoverUrl.Length == 0 && item.Cover is null && item.Thumbnail is null
            && item.ArtworkIdentity != onlineIdentity && events.Contains(nameof(LibrarySongItem.ArtworkIdentity)),
            "显式清空专辑导致有效封面URL失效，即使原始联网URL未变也清理旧图");

        item.ApplyDetails(local); item.Thumbnail = thumbnail; item.Cover = fullSize;
        string firstFile = item.ArtworkIdentity;
        events.Clear(); item.Update(item.Song with { Sources = ["C:\\Music\\replacement.mp3"] }, false, true);
        check(item.ArtworkIdentity != firstFile && item.Thumbnail is null && item.Cover is null && !item.MetadataLoaded
            && events.Contains(nameof(LibrarySongItem.ArtworkIdentity)),
            "本地封面来源替换使旧图片与旧异步身份失效，不把上一文件图片带给新来源");

        item.Thumbnail = thumbnail; item.Cover = fullSize;
        string replacedSource = item.ArtworkIdentity;
        events.Clear(); item.ApplyDetails(local with { LocalReadKey = "local-v2" });
        check(item.ArtworkIdentity != replacedSource && item.Thumbnail is null && item.Cover is null
            && events.Contains(nameof(LibrarySongItem.ArtworkIdentity)),
            "同一路径的本地文件签名变化会重新加载封面，不复用旧文件缩略图");

        item.ApplyDetails(online); item.Thumbnail = thumbnail; item.Cover = fullSize;
        string remoteIdentity = item.ArtworkIdentity;
        events.Clear(); item.Update(item.Song with { Sources = ["C:\\Music\\another-source.mp3"] }, false, true);
        check(item.ArtworkIdentity == remoteIdentity && ReferenceEquals(item.ListCover, thumbnail)
            && ReferenceEquals(item.Cover, fullSize) && !events.Contains(nameof(LibrarySongItem.ArtworkIdentity)),
            "已有有效联网封面时本地来源变化保留同一张图片，避免无关失效和重新解码");
    }
}
