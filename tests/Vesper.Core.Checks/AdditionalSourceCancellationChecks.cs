using System.Net;
using System.Text;
using System.Text.Json;
using Vesper.Core;

internal static class AdditionalSourceCancellationChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        await CheckIndependentWaitersAsync(check);
        await CheckCancelledFailureRetryAsync(check);
        await CheckCacheExpiryAsync(check);
        await CheckProviderTimeoutAsync(check);
        await CheckAlreadyCancelledAsync(check);
    }

    private static async Task CheckIndependentWaitersAsync(Action<bool, string> check)
    {
        const string title = "共享等待曲目";
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken requestToken = default;
        using var handler = new SourceHandler(title, async (_, token) =>
        {
            requestToken = token;
            started.TrySetResult();
            await release.Task.WaitAsync(token);
            return Json(NeteaseJson(title, 101));
        }, includeDeezerResult: true);
        using var http = new HttpClient(handler);
        var sources = CreateSources(http);
        using var firstCancellation = new CancellationTokenSource();
        using var laterCancellation = new CancellationTokenSource();
        var first = sources.SearchMetadata(title, firstCancellation.Token);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var continuing = sources.SearchMetadata(title, CancellationToken.None);
            var later = sources.SearchMetadata(title, laterCancellation.Token);
            firstCancellation.Cancel();
            laterCancellation.Cancel();
            check(await WasCancelledAsync(first) && await WasCancelledAsync(later),
                "多来源检索的真实调用取消保持取消状态，即使其他来源已返回有效歌曲也不返回部分成功");
            check(!requestToken.IsCancellationRequested,
                "首个或后加入等待者取消均不取消其他等待者共享的网易请求");
            release.TrySetResult();
            var result = await continuing.WaitAsync(TimeSpan.FromSeconds(5));
            check(result.Any(match => match.RecordingId == "netease:101") && handler.NeteaseRequests == 1,
                "同一歌名的并发等待者只发出一次共享网易请求，未取消者正常取得结果");
            var cached = await sources.SearchMetadata(title, CancellationToken.None);
            check(cached.Any(match => match.RecordingId == "netease:101") && handler.NeteaseRequests == 1,
                "等待者取消后成功完成的共享请求仍可在有效期内复用");
        }
        finally { release.TrySetResult(); }
    }

    private static async Task CheckCancelledFailureRetryAsync(Action<bool, string> check)
    {
        const string title = "瞬时失败后重试";
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fail = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new SourceHandler(title, async (attempt, token) =>
        {
            if (attempt == 1)
            {
                started.TrySetResult();
                await fail.Task.WaitAsync(token);
                return new(HttpStatusCode.ServiceUnavailable);
            }
            return Json(NeteaseJson(title, 202));
        });
        using var http = new HttpClient(handler);
        var sources = CreateSources(http);
        using var cancellation = new CancellationTokenSource();
        var cancelled = sources.SearchMetadata(title, cancellation.Token);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            check(await WasCancelledAsync(cancelled), "全部等待者离开后调用立即取消，无需等待共享请求超时");
            fail.TrySetResult();
            IReadOnlyList<SongMatch> retried = [];
            // An immediate retry may still join the first in-flight failure.
            // After observing it, the next call must create fresh work.
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    retried = await sources.SearchMetadata(title, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
                    break;
                }
                catch (HttpRequestException) when (attempt == 0) { }
            }
            check(handler.NeteaseRequests == 2 && retried.Any(match => match.RecordingId == "netease:202"),
                "全部等待者取消后共享任务若再失败也会移出缓存，瞬时故障后的调用能够重新检索成功");
        }
        finally { fail.TrySetResult(); }
    }

    private static async Task CheckCacheExpiryAsync(Action<bool, string> check)
    {
        const string title = "空结果缓存过期";
        var clock = new ManualClock();
        using var handler = new SourceHandler(title, (attempt, _) => Task.FromResult(
            Json(attempt == 1 ? "{\"result\":{\"songs\":[]}}" : NeteaseJson(title, attempt))));
        using var http = new HttpClient(handler);
        var sources = CreateSources(http, clock);
        var missing = await sources.SearchMetadata(title, CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(14));
        var stillCached = await sources.SearchMetadata(title, CancellationToken.None);
        check(missing.Count == 0 && stillCached.Count == 0 && handler.NeteaseRequests == 1,
            "成功的空检索结果在十五分钟内共享缓存，避免持续请求同一缺失曲目");
        clock.Advance(TimeSpan.FromMinutes(2));
        var refreshed = await sources.SearchMetadata(title, CancellationToken.None);
        check(handler.NeteaseRequests == 2 && refreshed.Any(match => match.RecordingId == "netease:2"),
            "空结果缓存十五分钟后失效，可取得服务后来补充的曲目，无需重启应用");
        clock.Advance(TimeSpan.FromMinutes(14));
        var positiveCached = await sources.SearchMetadata(title, CancellationToken.None);
        check(handler.NeteaseRequests == 2 && positiveCached.Any(match => match.RecordingId == "netease:2"),
            "刷新成功后的缓存有效期从完成时重新计算，正常结果继续复用");
        clock.Advance(TimeSpan.FromMinutes(2));
        var positiveRefreshed = await sources.SearchMetadata(title, CancellationToken.None);
        check(handler.NeteaseRequests == 3 && positiveRefreshed.Any(match => match.RecordingId == "netease:3"),
            "成功的非空检索缓存同样有有限有效期，不永久保留过期资料");
    }

    private static async Task CheckProviderTimeoutAsync(Action<bool, string> check)
    {
        const string title = "单源超时仍有结果";
        using var handler = new SourceHandler(title, (_, _) =>
            Task.FromException<HttpResponseMessage>(new OperationCanceledException("source timeout")), includeDeezerResult: true);
        using var http = new HttpClient(handler);
        var result = await CreateSources(http).SearchMetadata(title, CancellationToken.None);
        check(result.Any(match => match.RecordingId == "deezer:303"),
            "调用未取消时单一来源的超时不影响其他来源已取得的有效结果");
    }

    private static async Task CheckAlreadyCancelledAsync(Action<bool, string> check)
    {
        using var handler = new SourceHandler("无需发出请求", (_, _) => throw new InvalidOperationException("不应发请求"));
        using var http = new HttpClient(handler);
        var sources = CreateSources(http);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        check(await WasCancelledAsync(sources.SearchMetadata("无需发出请求", cancellation.Token)) && handler.AllRequests == 0,
            "预先取消的资料检索不启动任何来源请求");
        bool metadataCancelled = false;
        try { await sources.GetMetadata(new("netease:1", "", "歌名", "艺人", "专辑", "", ""), cancellation.Token); }
        catch (OperationCanceledException) { metadataCancelled = true; }
        check(metadataCancelled && handler.AllRequests == 0, "无需联网的备用源资料转换也遵守调用取消，不产生可被误保存的成功结果");
    }

    private static AdditionalSongSources CreateSources(HttpClient http, TimeProvider? clock = null) =>
        new(http, new MusicBrainzClient(http), new LrcLibClient(http), clock);

    private static async Task<bool> WasCancelledAsync(Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(5)); return false; }
        catch (OperationCanceledException) { return true; }
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static string NeteaseJson(string title, long id) => JsonSerializer.Serialize(new
    {
        result = new
        {
            songs = new[] { new { id, name = title, ar = new[] { new { name = "测试艺人" } },
                al = new { id = 11, name = "测试专辑", picUrl = "" }, dt = 200000, publishTime = 0 } }
        }
    });

    private sealed class SourceHandler(string title, Func<int, CancellationToken, Task<HttpResponseMessage>> netease,
        bool includeDeezerResult = false) : HttpMessageHandler
    {
        private int neteaseRequests, allRequests;
        internal int NeteaseRequests => Volatile.Read(ref neteaseRequests);
        internal int AllRequests => Volatile.Read(ref allRequests);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref allRequests);
            cancellationToken.ThrowIfCancellationRequested();
            return request.RequestUri!.Host switch
            {
                "music.163.com" => netease(Interlocked.Increment(ref neteaseRequests), cancellationToken),
                "musicbrainz.org" => Task.FromResult(Json("{\"recordings\":[]}")),
                "api.deezer.com" => Task.FromResult(Json(includeDeezerResult ? JsonSerializer.Serialize(new
                {
                    data = new[] { new { id = 303, title, artist = new { name = "测试艺人" },
                        album = new { id = 44, title = "测试专辑", cover_xl = "" } } }
                }) : "{\"data\":[]}")),
                _ => throw new InvalidOperationException("测试不允许访问未配置的来源：" + request.RequestUri.Host)
            };
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private readonly object gate = new();
        private DateTimeOffset current = new(2026, 11, 4, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() { lock (gate) return current; }
        internal void Advance(TimeSpan amount) { lock (gate) current += amount; }
    }
}
