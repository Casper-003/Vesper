using System.Net;
using System.Text;
using Vesper.Core;

internal static class OnlineArtistProfileChecks
{
    private const string ArtistId = "00000000-0000-0000-0000-000000000001";
    private const string ComposerId = "00000000-0000-0000-0000-000000000002";
    private const string RecordingId = "00000000-0000-0000-0000-000000000003";
    private const string WorkId = "00000000-0000-0000-0000-000000000004";

    public static async Task RunAsync(Action<bool, string> check)
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var musicBrainz = new MusicBrainzClient(http);
        var profiles = new ArtistProfileClient(http, musicBrainz);
        check(await profiles.GetAsync(new("musicbrainz", "artist", "not-an-id")) is null
            && await profiles.GetAsync(new("musicbrainz", "recording", RecordingId)) is null && handler.Requests.Count == 0,
            "艺人资料只查询已确认的艺人外部身份，无可靠 ID 不凭名称猜测");
        var profile = await profiles.GetAsync(new("musicbrainz", "artist", ArtistId));
        check(profile is { Name: "测试歌手", Description: "中文简介" }
            && profile.ImageUrl == "https://upload.wikimedia.org/test/512px-profile.jpg"
            && profile.SourceUrl == "https://www.wikidata.org/wiki/Q123"
            && profile.ImageSourceUrl.Contains("commons.wikimedia.org/wiki/File:"),
            "精确艺人身份沿 Wikidata 关系读取中文简介及图片并保留资料与图片出处");
        check(handler.Requests.Any(uri => uri.Host == "commons.wikimedia.org"
            && uri.Query.Contains("iiurlwidth=512") && uri.Query.Contains("iiurlheight=512")
            && Uri.UnescapeDataString(uri.Query).Contains("Preferred.jpg")),
            "艺人照片选择非弃用的优先 P18 图片，仅请求限尺寸缩略图");
        var deezer = await profiles.GetAsync(new("deezer", "artist", "42"));
        check(deezer?.ImageUrl == "https://cdn-images.dzcdn.net/artist/500x500.jpg"
            && deezer.SourceUrl == "https://www.deezer.com/artist/42",
            "备用资料源按精确艺人 ID 取得图片，不查询同名候选");
        var credits = await musicBrainz.GetRecordingCreditsAsync(RecordingId);
        check(credits.Count == 2 && credits.Single(credit => credit.Role == "performer").Name == "测试歌手"
            && credits.Single(credit => credit.Role == "composer").ExternalIds?.Single().Value == ComposerId
            && handler.Requests.Count(uri => uri.AbsolutePath.Contains("/work/")) == 1,
            "作曲家按录音与作品关系取得并去重，演唱者和作曲角色独立保存");
        handler.MismatchedIdentity = true;
        bool rejected = false;
        try { await profiles.GetAsync(new("musicbrainz", "artist", ArtistId)); }
        catch (InvalidDataException) { rejected = true; }
        check(rejected, "艺人资料响应身份不一致时拒绝绑定图片和简介");
        handler.MismatchedIdentity = false;
        handler.Oversized = true;
        rejected = false;
        try { await profiles.GetAsync(new("deezer", "artist", "42")); }
        catch (InvalidDataException) { rejected = true; }
        check(rejected, "后台艺人响应超过两 MiB 时提前停止，避免无界 JSON 缓冲");
        handler.Oversized = false;
        handler.NoThumbnail = true;
        profile = await profiles.GetAsync(new("musicbrainz", "artist", ArtistId));
        check(profile?.ImageUrl == "" && profile.Description == "中文简介",
            "无缩略图时保留简介，不回退下载无界原始照片");
        handler.FailRecording = true;
        int requestsBeforeMetadata = handler.Requests.Count;
        var metadata = await musicBrainz.GetMetadataAsync(new(RecordingId, "", "测试歌曲", "测试歌手", "", "", ""));
        check(metadata.Title == "测试歌曲" && metadata.Artist == "测试歌手" && handler.Requests.Count == requestsBeforeMetadata,
            "前台歌曲资料不额外查询作曲关系，作者资料由后台阶段独立补全");
        bool unavailable = false;
        try { await musicBrainz.GetRecordingCreditsAsync(RecordingId); }
        catch (HttpRequestException) { unavailable = true; }
        check(unavailable, "作曲关系服务失败向后台传播，以便冷却重试而非标记为查无资料");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        bool stopped = false;
        try { await profiles.GetAsync(new("deezer", "artist", "42"), canceled.Token); }
        catch (OperationCanceledException) { stopped = true; }
        check(stopped, "后台艺人资料查询响应取消，不继续发出请求");
    }

    private sealed class Handler : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        public bool MismatchedIdentity, Oversized, NoThumbnail, FailRecording;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!; Requests.Add(uri);
            if (FailRecording && uri.AbsolutePath.Contains("/recording/"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            string body = uri.Host switch
            {
                "www.wikidata.org" => """{"entities":{"Q123":{"descriptions":{"en":{"value":"English description"},"zh-hans":{"value":"中文简介"}},"claims":{"P18":[{"rank":"deprecated","mainsnak":{"snaktype":"value","datavalue":{"value":"Deprecated.jpg"}}},{"rank":"normal","mainsnak":{"snaktype":"value","datavalue":{"value":"Normal.jpg"}}},{"rank":"preferred","mainsnak":{"snaktype":"value","datavalue":{"value":"Preferred.jpg"}}}]}}}}""",
                "commons.wikimedia.org" => NoThumbnail
                    ? """{"query":{"pages":[{"imageinfo":[{"url":"https://upload.wikimedia.org/huge-original.jpg"}]}]}}"""
                    : """{"query":{"pages":[{"imageinfo":[{"thumburl":"https://upload.wikimedia.org/test/512px-profile.jpg","descriptionurl":"https://commons.wikimedia.org/wiki/File:Preferred.jpg"}]}]}}""",
                "api.deezer.com" => """{"id":42,"name":"测试歌手","picture_big":"https://cdn-images.dzcdn.net/artist/500x500.jpg"}""",
                _ when uri.AbsolutePath.Contains("/work/") => $$$$"""{"id":"{{{{WorkId}}}}","relations":[{"target-type":"artist","type":"composer","artist":{"id":"{{{{ComposerId}}}}","name":"测试作曲家"}},{"target-type":"artist","type":"composer","artist":{"id":"{{{{ComposerId}}}}","name":"测试作曲家"}},{"target-type":"artist","type":"lyricist","artist":{"id":"{{{{ArtistId}}}}","name":"测试词作者"}}]}""",
                _ when uri.AbsolutePath.Contains("/recording/") => $$$$"""{"id":"{{{{RecordingId}}}}","artist-credit":[{"name":"测试歌手","artist":{"id":"{{{{ArtistId}}}}","name":"测试歌手"}}],"relations":[{"target-type":"work","type":"performance","work":{"id":"{{{{WorkId}}}}"}},{"target-type":"work","type":"performance","work":{"id":"{{{{WorkId}}}}"}}]}""",
                _ => $$$$"""{"id":"{{{{(MismatchedIdentity ? ComposerId : ArtistId)}}}}","name":"测试歌手","disambiguation":"简短介绍","relations":[{"target-type":"url","type":"wikidata","url":{"resource":"https://www.wikidata.org/wiki/Q123"}}]}"""
            };
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            if (Oversized) response.Content.Headers.ContentLength = 3 * 1024 * 1024;
            return Task.FromResult(response);
        }
    }
}
