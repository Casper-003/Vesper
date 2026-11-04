using Vesper.App;
using Vesper.Core;

internal static class BackgroundVideoChecks
{
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        CheckIdentityAndEligibility(check);
        await CheckPublicationAsync(root, check);
    }

    private static void CheckIdentityAndEligibility(Action<bool, string> check)
    {
        var now = new DateTimeOffset(2026, 11, 4, 0, 0, 0, TimeSpan.Zero);
        var input = new SongLookupInput("Singer - Song.mp3", new(Local: new("Song", "Singer", "Album", DurationSeconds: 180)));
        string identity = SongVideoLookupState.Identity(input, "source-1");
        var visualOnly = input with { Details = input.Details with
        {
            Online = new("Song", "Singer", "Album", CoverUrl: "https://cdn-images.dzcdn.net/cover.jpg"),
            OnlineLyrics = new("Lyrics", "", "", false, "https://lrclib.net/api/get/1", "Song", "Singer", 180, true),
            Assets = new("artwork", now, false)
        } };
        check(SongVideoLookupState.Identity(visualOnly, "source-1") == identity
            && SongVideoLookupState.Identity(input, "source-2") != identity
            && SongVideoLookupState.Identity(input with { FileName = "Singer - Song (Live).mp3" }, "source-1") != identity
            && SongVideoLookupState.Identity(input with { Details = input.Details with { Local = input.Details.Local! with { DurationSeconds = 181 } } }, "source-1") != identity,
            "MV匹配身份绑定歌曲、版本文件名、时长及来源，单独封面和歌词变化不重复查询");
        var missing = new SongVideoLookupState(identity, now, LookupOutcome.NotFound);
        var failed = missing with { Outcome = LookupOutcome.Unavailable };
        var matched = missing with { Outcome = LookupOutcome.Matched };
        check(!missing.ShouldLookup(identity, now.AddHours(23)) && missing.ShouldLookup(identity, now.AddDays(1))
            && !failed.ShouldLookup(identity, now.AddSeconds(59)) && failed.ShouldLookup(identity, now.AddMinutes(1))
            && !matched.ShouldLookup(identity, now.AddYears(1)) && matched.ShouldLookup("new-identity", now),
            "后台MV查无可靠匹配冷却一天、服务失败一分钟，成功关联不随时间重复替换");
        check(SongVideoLookupState.CanSearch(input, "Song.MP3") && !SongVideoLookupState.CanSearch(input, "Song.mp4")
            && !SongVideoLookupState.CanSearch(input, "Song.mkv")
            && !SongVideoLookupState.CanSearch(input with { Details = new(Local: new("Song", DurationSeconds: 180)) }, "Song.mp3")
            && !SongVideoLookupState.CanSearch(input with { Details = new(Local: new("Song", "Singer")) }, "Song.mp3")
            && !SongVideoLookupState.CanSearch(input with { Details = input.Details with { Video = new("BV0000000001", "Song Singer MV", "Uploader", 180) } }, "Song.mp3"),
            "整库后台只为身份与时长已知的音频找MV，已有本地视频或匹配时不额外查询");
        MusicVideoMatch[] candidates =
        [
            new("BV0000000001", "Song Singer Live MV", "Uploader", 180),
            new("BV0000000002", "Song Singer 饭制 MV", "Uploader", 180),
            new("BV0000000003", "Song Singer Official MV", "Uploader", 220),
            new("BV0000000004", "Song Singer Official MV", "Uploader", 181)
        ];
        check(BilibiliVideoClient.ChooseReliable(candidates, "Song", "Singer", 180, input.FileName)?.Bvid == "BV0000000004"
            && BilibiliVideoClient.ChooseReliable(candidates[..3], "Song", "Singer", 180, input.FileName) is null,
            "后台MV仍核对歌名、艺人、演唱版本和时长，排除现场、饭制及不同时长候选");
    }

    private static async Task CheckPublicationAsync(string root, Action<bool, string> check)
    {
        string folder = Path.Combine(root, "background-video-media"), store = Path.Combine(root, "background-video.json");
        Directory.CreateDirectory(folder);
        string media = Path.Combine(folder, "Singer - Song.mp3");
        await File.WriteAllTextAsync(media, "Background MV fixture: existence-only synthetic audio");
        var video = new MusicVideoMatch("BV0000000004", "Song Singer Official MV", "Uploader", 181);
        string id, sourceKey;
        using (var model = new MainViewModel(store))
        {
            await model.InitializeAsync();
            await model.AddDirectoryAsync(folder);
            var song = model.Results.Single();
            id = song.Song.Id; sourceKey = model.GetEnrichmentSourceKey(id)!;
            await model.SaveBackgroundLocalMetadataAsync(id, sourceKey,
                new(media, new("Song", "Singer", "Album", Lyrics: "Cached lyrics", DurationSeconds: 180)), () => true);
            await model.FlushBackgroundMetadataAsync();
            var expected = model.GetLookupInput(id)!;
            long revision = model.IndexRevision;
            check(!await model.SaveBackgroundVideoAsync(id, sourceKey, expected, video, LookupOutcome.Matched, () => false)
                && model.Library.Details[id].Video is null && model.Library.Details[id].VideoLookup is null,
                "失效发布票据不能保存后台MV关联或请求状态");
            check(await model.SaveBackgroundVideoAsync(id, sourceKey, expected, null, LookupOutcome.NotFound, () => true)
                && model.Library.Details[id].VideoLookup is { Outcome: LookupOutcome.NotFound }
                && model.Library.Details[id].Video is null && model.IndexRevision == revision,
                "无可靠MV可持久记录独立负缓存，不生成假关联或触发索引重建");
            check(await model.FlushBackgroundMetadataAsync(), "后台MV匹配状态通过合并资料写入持久化");
        }
        using (var model = new MainViewModel(store))
        {
            await model.InitializeAsync();
            var expected = model.GetLookupInput(id)!;
            var negative = model.Library.Details[id].VideoLookup;
            check(negative is { Outcome: LookupOutcome.NotFound }
                && !negative.ShouldLookup(SongVideoLookupState.Identity(expected, sourceKey), DateTimeOffset.UtcNow),
                "重启复用后台MV无匹配状态，避免每次打开软件重新检索同一首歌");
            await model.EnsureSearchIndexAsync();
            long revision = model.IndexRevision;
            check(await model.SaveBackgroundVideoAsync(id, sourceKey, expected, video, LookupOutcome.Matched, () => true)
                && model.FindSong(id)?.MatchedVideoUrl == video.Url && model.IndexRevision == revision,
                "可靠后台MV关联发布到共享歌曲并保留原搜索索引");
            check(!await model.SaveBackgroundVideoAsync(id, sourceKey, expected,
                    video with { Bvid = "BV0000000005" }, LookupOutcome.Matched, () => true)
                && model.Library.Details[id].Video == video,
                "已确认的MV关联优先，晚到的其他自动候选不能覆盖");
            await model.FlushBackgroundMetadataAsync();
        }
        using (var model = new MainViewModel(store))
        {
            await model.InitializeAsync();
            check(model.Library.Details[id].Video == video && model.Library.Details[id].VideoLookup is { Outcome: LookupOutcome.Matched },
                "重启恢复已确认的后台MV及成功状态，关联不依赖打开详情");
            var stale = model.GetLookupInput(id)!;
            await model.SaveCorrectionsAsync(id, "New song", "New artist", "");
            check(!await model.SaveBackgroundVideoAsync(id, sourceKey, stale, video, LookupOutcome.Matched, () => true)
                && model.Library.Details[id].Video is null,
                "手动更正歌名和艺人后，旧曲目后台MV结果不能重新绑定");
            var current = model.GetLookupInput(id)!;
            await model.RemoveDirectoryAsync(folder);
            check(!await model.SaveBackgroundVideoAsync(id, sourceKey, current, video, LookupOutcome.Matched, () => true)
                && model.Library.Details[id].Video is null,
                "移除来源后忽略取消的后台MV结果仍没有发布权");
        }
    }
}
