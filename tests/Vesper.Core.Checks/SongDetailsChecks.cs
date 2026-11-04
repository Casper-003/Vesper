using System.Net;
using System.Text;
using Vesper.App;
using Vesper.Core;

internal static class SongDetailsChecks
{
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        string folder = Path.Combine(root, "details-media"), path = Path.Combine(root, "details.json");
        Directory.CreateDirectory(folder);
        string media = Path.Combine(folder, "歌手 - 原歌名 [1080P] (现场版).mp3");
        await File.WriteAllTextAsync(media, "untouched original audio");
        using var model = new MainViewModel(path);
        await model.InitializeAsync(); await model.AddDirectoryAsync(folder);
        var row = model.Results.Single();
        string id = row.Song.Id;
        check(row.HasNoDetails && !row.HasArtist && row.Artist == "未知艺人", "无详情时显示缺失状态，未知艺人不冒充已获取的详情");
        model.ApplyLocalMetadata(row, media, new(Artist: "本地艺人", Year: "2020", Lyrics: "第一行\n第二行", DurationSeconds: 200));
        check(row.HasArtist && row.HasYear && !row.HasAlbum && !row.HasNoDetails && row.HasLyrics,
            "仅有部分详情时保留已知字段、隐藏缺失字段，并显示内嵌歌词");
        await model.SaveOnlineMetadataAsync(id, new("线上歌名", "线上艺人", "线上专辑", "唱片公司", "2021",
            SourceUrl: "https://musicbrainz.org/recording/00000000-0000-0000-0000-000000000001"));
        check(row.Title == "线上歌名" && row.ArtistDetail == "线上艺人" && row.Publisher == "唱片公司" && row.Lyrics.Contains("第二行"),
            "选定联网资料与本地歌词合并，不丢弃已有歌词");
        await model.ToggleFavoriteAsync(row);
        await model.SaveCorrectionsAsync(id, "  我的歌名  ", "我的艺人", "");
        await model.SaveOnlineMetadataAsync(id, new("其他歌名", "其他艺人", "其他专辑", Year: "2022"));
        await model.RefreshAsync();
        check(ReferenceEquals(row, model.FindSong(id)) && row.Title == "我的歌名" && row.ArtistDetail == "我的艺人"
            && !row.HasAlbum && row.Year == "2022" && model.FavoriteResults.Single().Title == "我的歌名",
            "刷新及再次联网补全保留手动修正和显式清空字段，曲库、收藏、详情共享结果");
        model.AddToQueue(row);
        check(model.Queue.Pending.Single().Title == "我的歌名" && await File.ReadAllTextAsync(media) == "untouched original audio",
            "入队使用修正后的歌名，原媒体文件保持原样");
        using var reopened = new MainViewModel(path);
        await reopened.InitializeAsync();
        check(reopened.Results.Single().Title == "我的歌名" && reopened.Results.Single().ArtistDetail == "我的艺人"
            && reopened.Results.Single().HasSource == row.HasSource && !reopened.Results.Single().HasAlbum,
            "跨启动恢复缓存资料、手动修正和已清空字段，无需重新联网");
        check(!await model.SaveCorrectionsAsync(id, "  ", "", "") && row.Title == "我的歌名", "拒绝空歌名，不改变已保存资料");
        var video = new MusicVideoMatch("BV1zx411R7ro", "我的艺人 - 我的歌名 MV", "上传者", 200);
        check(!await model.SaveVideoMatchAsync(id, video, "过期歌名", "我的艺人") && !row.HasMatchedVideo,
            "旧曲目身份的后台 MV 结果不能覆盖新修正");
        await model.SaveVideoMatchAsync(id, video, "我的歌名", "我的艺人");
        check(row.HasMatchedVideo, "后台确认的 MV 关联可保存，不要求用户逐个检查候选");
        Directory.CreateDirectory(path + ".tmp");
        check(!await model.SaveCorrectionsAsync(id, "保存不了", "错误艺人", "错误专辑")
            && row.Title == "我的歌名" && row.ArtistDetail == "我的艺人" && row.HasMatchedVideo,
            "写入失败恢复原资料和 MV 关联，不残留未保存的修正");

        SongSearchHint hint = SongSearchHint.From("歌手 - 歌名 [1080P] (现场版)", new());
        check(hint.Artist == "歌手" && hint.Title == "歌名" && hint.FileName.Contains("现场版") && hint.FileName.Contains("1080P"),
            "资料检索词忽略画质和现场后缀，原始文件名继续保留版本核对线索");

        MusicVideoMatch original = new("BV1zx411R7ro", "艺人 - 歌名 官方 MV", "官方", 200);
        MusicVideoMatch[] candidates = [original with { Title = "艺人 - 歌名 翻唱 MV" },
            original with { Title = "艺人 - 歌名 现场 LIVE MV" }, original with { DurationSeconds = 250 }, original];
        check(BilibiliVideoClient.ChooseReliable(candidates, "歌名", "艺人", 200, "艺人 - 歌名.mp3") == original,
            "MV 自动筛选同时核对曲名、艺人、版本和时长，跳过翻唱、现场及不同时长");
        check(BilibiliVideoClient.ChooseReliable([original with { Title = "艺人 - 歌名续篇 MV" }], "歌名", "艺人", 200, "歌名") is null
            && BilibiliVideoClient.ChooseReliable(candidates, "歌名", "", 200, "歌名") is null
            && BilibiliVideoClient.ChooseReliable(candidates, "歌名", "艺人", 0, "歌名") is null,
            "相似歌名及缺少艺人或时长时不猜测采用 MV，保留未匹配状态");

        await CheckApiAsync(check);
        await CheckScanDuringEditAsync(root, check);
    }

    private static async Task CheckApiAsync(Action<bool, string> check)
    {
        const string recording = "11111111-1111-1111-1111-111111111111", release = "22222222-2222-2222-2222-222222222222";
        using var http = new HttpClient(new FixtureHandler(request => request.RequestUri!.AbsolutePath.Contains("release/")
            ? """{"date":"2020-02-03","label-info":[{"label":{"name":"测试唱片"}}]}"""
            : $$"""{"recordings":[{"id":"{{recording}}","title":"歌名","artist-credit":[{"name":"歌手"}],"releases":[{"id":"{{release}}","title":"原版专辑","date":"2020"},{"id":"33333333-3333-3333-3333-333333333333","title":"合辑","date":"2024"}]}]}"""));
        var lookup = new MusicBrainzClient(http);
        var results = await lookup.SearchAsync("歌名", "歌手");
        check(results.Count == 2 && results[0].Album != results[1].Album,
            "资料检索保留同曲不同专辑候选，不把搜索第一项静默写入曲库");
        var metadata = await lookup.GetMetadataAsync(results[0]);
        check(metadata.Publisher == "测试唱片" && metadata.Year == "2020"
            && metadata.SourceUrl.Contains(recording) && metadata.CoverUrl.Contains(release),
            "选定专辑后读取唱片公司和年份，并保存资料及封面来源");
        check(metadata.ExternalIds.Any(id => id.Kind == "recording" && id.Value == recording)
            && !metadata.ExternalIds.Any(id => id.Kind == "release") && metadata.DiscNumber is null && metadata.TrackNumber is null,
            "MusicBrainz描述性专辑候选不冒作本地文件已确认发行版，也不生成曲序");
        using var biliHttp = new HttpClient(new FixtureHandler(_ => """{"code":0,"data":{"result":[{"bvid":"BV1zx411R7ro","title":"<em class=\"keyword\">歌手</em> - 歌名 &amp; MV","author":"上传者","duration":"3:20"}]}}"""));
        var videos = await new BilibiliVideoClient(biliHttp).SearchAsync("歌名", "歌手");
        check(videos.Single().Title == "歌手 - 歌名 & MV" && videos[0].DurationSeconds == 200,
            "B 站结果按纯文本呈现并解析时长，不执行结果中的 HTML");
        using var unavailable = new HttpClient(new FixtureHandler(_ => "{\"code\":-412}"));
        bool rejected = false;
        try { await new BilibiliVideoClient(unavailable).SearchAsync("歌名", "歌手"); }
        catch (HttpRequestException) { rejected = true; }
        check(rejected, "B 站接口异常明确作为检索失败，不冒充无匹配或自动采用候选");
    }

    private static async Task CheckScanDuringEditAsync(string root, Action<bool, string> check)
    {
        bool hold = false;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<ScanResult> Scan(string[] directories, Song[] previous, IReadOnlyDictionary<string, FileScanCache> cache,
            IProgress<ScanProgress> progress, CancellationToken token)
        {
            if (hold) { started.TrySetResult(); await release.Task; }
            return await LibraryScanner.ScanAsync(directories, previous, token, cache, progress);
        }
        using var model = new MainViewModel(Path.Combine(root, "details-scan.json"), Scan);
        await model.InitializeAsync(); await model.AddDirectoryAsync(Path.Combine(root, "details-media"));
        var row = model.Results.Single();
        hold = true;
        Task scanning = model.RefreshAsync(); await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await model.SaveCorrectionsAsync(row.Song.Id, "扫描中修正", "新艺人", "新专辑");
        hold = false; release.TrySetResult(); await scanning.WaitAsync(TimeSpan.FromSeconds(10));
        check(row.Title == "扫描中修正" && row.ArtistDetail == "新艺人"
            && (await model.Store.LoadAsync()).Details[row.Song.Id].Corrections?.Title == "扫描中修正",
            "扫描期间可修正资料，旧扫描完成后仍保留并保存最新修正");
    }

    private sealed class FixtureHandler(Func<HttpRequestMessage, string> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(response(request), Encoding.UTF8, "application/json") });
        }
    }
}
