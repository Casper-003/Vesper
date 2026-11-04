using System.Net;
using System.Text;
using Vesper.Core;

internal static class AdditionalSourceChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var sources = new AdditionalSongSources(http, new(http), new(http));
        var metadataTask = sources.SearchMetadata("测试曲目", default);
        var lyricsTask = sources.SearchLyrics("测试曲目", default);
        await Task.WhenAll(metadataTask, lyricsTask);
        var match = (await metadataTask).Single();
        var metadata = await sources.GetMetadata(match, default);
        check(handler.Searches == 1 && metadata.Artist == "测试艺人" && metadata.CoverUrl.StartsWith("https://")
            && metadata.SourceUrl.Contains("music.163.com"), "主资料源失败时备用源返回资料和封面，资料与歌词共享一次歌曲搜索");
        var lyrics = LrcLibClient.ToLyrics((await lyricsTask).Single(), 200, "测试曲目.mp3");
        check(lyrics.PlainText == "合成测试文本" && lyrics.SourceUrl.Contains("music.163.com"),
            "主歌词源不可用时备用歌词仍可用，缓存保留实际来源");
        using var thirdHandler = new Handler { Third = true };
        using var thirdHttp = new HttpClient(thirdHandler);
        var third = new AdditionalSongSources(thirdHttp, new(thirdHttp), new(thirdHttp));
        var thirdMetadata = await third.SearchMetadata("测试曲目", default);
        var thirdDetails = await third.GetMetadata(thirdMetadata.Single(), default);
        check(thirdDetails.SourceUrl.Contains("deezer.com")
            && thirdDetails.ArtistCredits.Single().ExternalIds!.Single() == new MusicExternalId("deezer", "artist", "42"),
            "第三资料源保留精确艺人身份供图片同步，不丢失曲目来源");
        var thirdLyrics = await third.SearchLyrics("测试曲目", default);
        check(LrcLibClient.ToLyrics(thirdLyrics.Single(), 200, "测试曲目.mp3").PlainText == "合成测试文本"
            && thirdLyrics.Single().SourceUrl.Contains("kugou.com"), "第三歌词源可独立检索与解码，不依赖前两个来源成功");
    }
    private sealed class Handler : HttpMessageHandler
    {
        public int Searches;
        public bool Third;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host == "musicbrainz.org") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            if (Third && request.RequestUri.Host == "lrclib.net") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            string body;
            if (request.RequestUri.AbsolutePath.Contains("cloudsearch"))
            {
                Interlocked.Increment(ref Searches);
                if (Third) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                body = """{"result":{"songs":[{"id":42,"name":"测试曲目","ar":[{"name":"测试艺人"}],"al":{"id":7,"name":"测试专辑","picUrl":"http://p2.music.126.net/test.jpg"},"dt":200000,"publishTime":1577836800000}]},"code":200}""";
            }
            else if (Third && request.RequestUri.Host == "api.deezer.com") body = """{"data":[{"id":1,"title":"测试曲目","artist":{"id":42,"name":"测试艺人"},"album":{"id":1,"title":"测试专辑","cover_xl":"https://cdn-images.dzcdn.net/test.jpg"}}]}""";
            else if (Third && request.RequestUri.Host == "lyrics.kugou.com") body = request.RequestUri.AbsolutePath.Contains("download")
                ? "{\"content\":\"" + Convert.ToBase64String(Encoding.UTF8.GetBytes("[00:01.00]合成测试文本")) + "\"}"
                : """{"candidates":[{"id":"9","song":"测试曲目","singer":"测试艺人","duration":200000,"accesskey":"synthetic"}]}""";
            else body = """{"lrc":{"lyric":"[00:01.00]合成测试文本"},"code":200}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
