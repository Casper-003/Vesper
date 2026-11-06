using System.Net;
using Vesper.Core;

internal static class ArtworkDiskCacheChecks
{
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        string directory = Path.Combine(root, "artwork-cache");
        int requests = 0;
        using var client = new HttpClient(new Handler((_, _) =>
        {
            Interlocked.Increment(ref requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3, 4]) });
        }));
        var cache = new ArtworkDiskCache(client, directory);
        var url = new Uri("https://coverartarchive.org/release/test/front-500");
        string path = await cache.GetAsync(url);
        string again = await cache.GetAsync(url);
        check(path == again && requests == 1 && (await File.ReadAllBytesAsync(path)).SequenceEqual(new byte[] { 1, 2, 3, 4 })
            && Path.GetFileNameWithoutExtension(path).Length == 64 && !Directory.EnumerateFiles(directory, "*.tmp").Any(),
            "图片缓存用原URL哈希兼容磁盘记录，重复获取只读磁盘且原子发布后无临时文件");

        check(ArtworkDiskCache.IsAllowed(new Uri("https://p1.music.126.net/a"))
            && ArtworkDiskCache.IsAllowed(new Uri("https://cdn-images.dzcdn.net/a"))
            && ArtworkDiskCache.IsAllowed(new Uri("https://commons.wikimedia.org/wiki/Special:FilePath/Test.jpg?width=600"))
            && ArtworkDiskCache.IsAllowed(new Uri("https://upload.wikimedia.org/wikipedia/commons/test.jpg"))
            && ArtworkDiskCache.IsAllowed(new Uri("https://thumb.wikimedia.org/wikipedia/commons/thumb/test.jpg"))
            && !ArtworkDiskCache.IsAllowed(new Uri("https://thumb.wikimedia.org.example.com/test.jpg"))
            && !ArtworkDiskCache.IsAllowed(new Uri("http://coverartarchive.org/a"))
            && !ArtworkDiskCache.IsAllowed(new Uri("https://coverartarchive.org.example.com/a"))
            && !ArtworkDiskCache.IsAllowed(new Uri("https://example.com/a"))
            && !ArtworkDiskCache.IsAllowed(new Uri("https://user@coverartarchive.org/a"))
            && !ArtworkDiskCache.IsAllowed(new Uri("https://coverartarchive.org:444/a")),
            "图片只接受已接入来源的HTTPS地址，拒绝相似恶意域名、凭据和非标准端口");
        await File.WriteAllBytesAsync(path, []);
        await cache.GetAsync(url);
        check(requests == 2 && new FileInfo(path).Length == 4, "空的旧图片缓存会重新下载，不永久保留损坏占位");

        await CheckConcurrencyAsync(root, check);
        await CheckFailuresAsync(root, check);
        await CheckMissingCacheAsync(root, check);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task CheckConcurrencyAsync(string root, Action<bool, string> check)
    {
        int started = 0, active = 0, maximum = 0, maximumBuffer = 0;
        var twoStarted = Signal(); var release = Signal();
        using var client = new HttpClient(new Handler((_, _) =>
        {
            int count = Interlocked.Increment(ref active);
            int seen;
            do { seen = maximum; } while (count > seen && Interlocked.CompareExchange(ref maximum, count, seen) != seen);
            if (Interlocked.Increment(ref started) == 2) twoStarted.TrySetResult();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new GeneratedStream(48 * 1024, release.Task,
                    () => Interlocked.Decrement(ref active), length => maximumBuffer = Math.Max(maximumBuffer, length)))
            });
        }));
        var cache = new ArtworkDiskCache(client, Path.Combine(root, "artwork-concurrency"));
        // Select different stripes so the first two requests can use both stream slots.
        var urls = Enumerable.Range(0, 100).Select(i => new Uri("https://coverartarchive.org/release/bounded-" + i + "/front-500"))
            .GroupBy(uri => Convert.ToByte(Path.GetFileName(cache.GetCachePath(uri))[..2], 16) % 16)
            .Take(8).Select(group => group.First()).ToArray();
        var tasks = urls.Select(uri => cache.GetAsync(uri)).ToArray();
        await twoStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var duplicate = cache.GetAsync(urls[0]);
        check(started == 2 && active == 2, "图片缓存同时仅有两条网络流，其他图片等待而不创建额外响应缓冲");
        release.TrySetResult();
        await Task.WhenAll(tasks.Append(duplicate)).WaitAsync(TimeSpan.FromSeconds(5));
        check(maximum == 2 && active == 0 && started == urls.Length && maximumBuffer <= 16384
            && await duplicate == await tasks[0],
            "图片下载始终保持最多两路和16KiB读取块，同URL并发请求合并且不保留已完成任务");
    }

    private static async Task CheckFailuresAsync(string root, Action<bool, string> check)
    {
        string directory = Path.Combine(root, "artwork-failures");
        int maximumBuffer = 0;
        using var oversizedClient = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new GeneratedStream(ArtworkDiskCache.MaximumBytes + 1, observe: length => maximumBuffer = Math.Max(maximumBuffer, length)))
        })));
        var cache = new ArtworkDiskCache(oversizedClient, directory);
        var url = new Uri("https://coverartarchive.org/release/oversized/front-500");
        bool rejected = false;
        try { await cache.GetAsync(url); } catch (InvalidDataException) { rejected = true; }
        check(rejected && maximumBuffer <= 16384 && !Directory.EnumerateFiles(directory).Any(),
            "无Content-Length的超8MiB图片仍被流式上限拒绝，清理部分文件且不分配整图数组");

        var entered = Signal(); var release = Signal();
        using var cancellationClient = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(new GeneratedStream(10, release.Task, observe: _ => entered.TrySetResult())) })));
        var cancelCache = new ArtworkDiskCache(cancellationClient, directory);
        using var cancellation = new CancellationTokenSource();
        var pending = cancelCache.GetAsync(url, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        bool cancelled = false;
        try { await pending; } catch (OperationCanceledException) { cancelled = true; }
        check(cancelled && !Directory.EnumerateFiles(directory).Any(), "取消图片下载释放流及并发位置，清理未完成磁盘文件");

        release.TrySetResult();
        await cancelCache.GetAsync(url);
        check(new FileInfo(cancelCache.GetCachePath(url)).Length == 10, "取消不记为成功或永久失败，相同图片稍后可以正常缓存");

        using var errorClient = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))));
        var failedCache = new ArtworkDiskCache(errorClient, directory);
        var failedUrl = new Uri("https://coverartarchive.org/release/failure/front-500");
        bool failed = false;
        try { await failedCache.GetAsync(failedUrl); } catch (HttpRequestException) { failed = true; }
        check(failed && !File.Exists(failedCache.GetCachePath(failedUrl)) && !Directory.EnumerateFiles(directory, "*.tmp").Any(),
            "HTTP失败不发布成功缓存、不留下临时文件");

        using var htmlClient = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("<html>upstream error</html>", System.Text.Encoding.UTF8, "text/html") })));
        var htmlCache = new ArtworkDiskCache(htmlClient, directory);
        bool htmlRejected = false;
        try { await htmlCache.GetAsync(failedUrl); } catch (InvalidDataException) { htmlRejected = true; }
        check(htmlRejected && !File.Exists(htmlCache.GetCachePath(failedUrl)) && !Directory.EnumerateFiles(directory, "*.tmp").Any(),
            "HTTP200的HTML错误页拒绝写入图片缓存，避免错误响应永久占用头像缓存");
    }

    private static async Task CheckMissingCacheAsync(string root, Action<bool, string> check)
    {
        foreach (var status in new[] { HttpStatusCode.NotFound, HttpStatusCode.Gone })
        {
            int requests = 0;
            bool available = false;
            var clock = new ManualClock();
            using var client = new HttpClient(new Handler((_, _) =>
            {
                requests++;
                return Task.FromResult(available ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2]) }
                    : new HttpResponseMessage(status));
            }));
            string directory = Path.Combine(root, "artwork-missing-" + (int)status);
            var cache = new ArtworkDiskCache(client, directory, clock);
            var url = new Uri("https://coverartarchive.org/release/missing/front-1200");
            async Task<bool> MissingAsync(ArtworkDiskCache current)
            {
                try { await current.GetAsync(url); return false; }
                catch (HttpRequestException ex) { return ex.StatusCode == status; }
            }
            bool firstMissing = await MissingAsync(cache);
            clock.Advance(TimeSpan.FromHours(23));
            bool repeatedMissing = await MissingAsync(new ArtworkDiskCache(client, directory, clock));
            check(firstMissing && repeatedMissing && requests == 1 && !Directory.EnumerateFiles(directory, "*.tmp").Any(),
                $"图片{(int)status}结果持久缓存一天，新缓存实例重复请求保持原状态且不再次联网");
            clock.Advance(TimeSpan.FromHours(1));
            bool expiredMissing = await MissingAsync(cache);
            check(expiredMissing && requests == 2, $"图片{(int)status}负缓存到期后才重新联网检查，不永久阻止恢复");

            await File.WriteAllBytesAsync(cache.GetCachePath(url), [3]);
            string cached = await cache.GetAsync(url);
            check(requests == 2 && new FileInfo(cached).Length == 1, $"图片{(int)status}负缓存不会盖过已有有效图片");
            File.Delete(cached);
            clock.Advance(TimeSpan.FromDays(1));
            available = true;
            await cache.GetAsync(url);
            check(requests == 3 && new FileInfo(cached).Length == 2 && !File.Exists(cached + ".missing"),
                $"图片{(int)status}来源恢复后可重新缓存，成功图片移除旧负缓存标记");
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset current = new(2026, 11, 4, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => current;
        internal void Advance(TimeSpan amount) => current += amount;
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class GeneratedStream(long length, Task? hold = null, Action? onDispose = null, Action<int>? observe = null) : Stream
    {
        private long remaining = length;
        private bool disposed;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            observe?.Invoke(buffer.Length);
            if (hold is not null) await hold.WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            int count = (int)Math.Min(buffer.Length, remaining);
            buffer.Span[..count].Clear();
            remaining -= count;
            return count;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (!disposed) { disposed = true; onDispose?.Invoke(); }
            base.Dispose(disposing);
        }
    }
}
