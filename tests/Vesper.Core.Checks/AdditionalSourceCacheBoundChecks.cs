using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Vesper.Core;

internal static class AdditionalSourceCacheBoundChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var clock = new ManualClock();
        using var handler = new CacheHandler();
        using var http = new HttpClient(handler);
        var sources = new AdditionalSongSources(http, new MusicBrainzClient(http), new LrcLibClient(http), clock);
        string Title(int index) => (index % 2 == 0 ? "正结果缓存" : "空结果缓存") + index;
        for (int i = 0; i < 64; i++) await TouchAsync(sources, Title(i));
        await TouchAsync(sources, Title(0));
        await TouchAsync(sources, Title(64));
        await TouchAsync(sources, Title(1));
        await TouchAsync(sources, Title(63));
        await TouchAsync(sources, Title(2));
        check(handler.Requests[Title(0)] == 1 && handler.Requests[Title(63)] == 1
            && handler.Requests[Title(1)] == 2 && handler.Requests[Title(2)] == 2,
            "网易检索最多缓存64个已完成歌名，成功和空结果都按最近使用淘汰，最近命中继续复用");

        clock.Advance(TimeSpan.FromMinutes(16));
        await TouchAsync(sources, Title(65));
        await TouchAsync(sources, Title(63));
        await TouchAsync(sources, Title(63));
        check(handler.Requests[Title(63)] == 2,
            "访问其他歌名时也清理十五分钟过期缓存，过期歌名重新获取后可正常复用");

        using var heldCancellation = new CancellationTokenSource();
        var held = sources.SearchMetadata("共享进行中", heldCancellation.Token);
        await handler.HeldStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (int i = 66; i < 136; i++) await TouchAsync(sources, Title(i));
        var join = sources.SearchMetadata("共享进行中", CancellationToken.None);
        heldCancellation.Cancel();
        await ObserveCancellationAsync(held);
        check(handler.Requests["共享进行中"] == 1 && !handler.HeldToken.IsCancellationRequested,
            "缓存容量压力保留正在执行的共享请求，取消单个等待者不取消请求或启动重复检索");
        handler.ReleaseHeld.TrySetResult();
        var joined = await join.WaitAsync(TimeSpan.FromSeconds(5));
        check(handler.Requests["共享进行中"] == 1 && joined.Any(match => match.RecordingId == "netease:10"),
            "满缓存中的共享检索完成后仍向未取消等待者交付结果");
    }

    private static async Task TouchAsync(AdditionalSongSources sources, string title)
    {
        // The fake Netease response completes synchronously. Cancelling the caller
        // afterwards releases unrelated provider rate-limit waits while retaining
        // the independently completed shared cache entry; no sleeps or network IO.
        using var cancellation = new CancellationTokenSource();
        var pending = sources.SearchMetadata(title, cancellation.Token);
        cancellation.Cancel();
        await ObserveCancellationAsync(pending);
    }

    private static async Task ObserveCancellationAsync(Task pending)
    {
        try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException) { }
    }

    private sealed class CacheHandler : HttpMessageHandler
    {
        internal readonly ConcurrentDictionary<string, int> Requests = new();
        internal readonly TaskCompletionSource HeldStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ReleaseHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationToken HeldToken;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string host = request.RequestUri!.Host;
            if (host == "musicbrainz.org") return Json("{\"recordings\":[]}");
            if (host == "api.deezer.com") return Json("{\"data\":[]}");
            if (host != "music.163.com") throw new InvalidOperationException("Unexpected test provider: " + host);
            string title = Uri.UnescapeDataString(request.RequestUri.Query.TrimStart('?').Split('&')
                .Single(part => part.StartsWith("s=", StringComparison.Ordinal))[2..]);
            Requests.AddOrUpdate(title, 1, (_, previous) => previous + 1);
            if (title == "共享进行中")
            {
                HeldToken = cancellationToken;
                HeldStarted.TrySetResult();
                await ReleaseHeld.Task.WaitAsync(cancellationToken);
            }
            return title.StartsWith("空结果", StringComparison.Ordinal) ? Json("{\"result\":{\"songs\":[]}}")
                : Json(JsonSerializer.Serialize(new
                {
                    result = new
                    {
                        songs = new[] { new { id = 10, name = title, ar = new[] { new { name = "缓存艺人" } },
                            al = new { id = 11, name = "缓存专辑", picUrl = "" }, dt = 200000, publishTime = 0 } }
                    }
                }));
        }

        private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK)
        { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset current = new(2026, 11, 4, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => current;
        internal void Advance(TimeSpan amount) => current += amount;
    }
}
