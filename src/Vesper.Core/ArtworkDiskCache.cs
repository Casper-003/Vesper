using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using System.Net;

namespace Vesper.Core;

/// <summary>
/// Stores encoded artwork without retaining image bytes or decoded bitmaps in memory.
/// Share one instance between foreground artwork and background prefetching.
/// </summary>
public sealed class ArtworkDiskCache(HttpClient client, string directory, TimeProvider? timeProvider = null)
{
    public const long MaximumBytes = 8 * 1024 * 1024;
    private const int BufferSize = 16 * 1024;
    private readonly SemaphoreSlim downloads = new(2, 2);
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    // A fixed number of locks coalesces identical URLs without growing a task/key cache
    // for every image in a large library. Collisions only delay another download.
    private readonly SemaphoreSlim[] stripes = Enumerable.Range(0, 16).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    public static bool IsAllowed(Uri url)
    {
        if (!url.IsAbsoluteUri || url.Scheme != Uri.UriSchemeHttps || !url.IsDefaultPort || url.UserInfo.Length > 0) return false;
        string host = url.IdnHost;
        return host.Equals("coverartarchive.org", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".music.126.net", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".dzcdn.net", StringComparison.OrdinalIgnoreCase)
            || host.Equals("commons.wikimedia.org", StringComparison.OrdinalIgnoreCase)
            || host.Equals("thumb.wikimedia.org", StringComparison.OrdinalIgnoreCase)
            || host.Equals("upload.wikimedia.org", StringComparison.OrdinalIgnoreCase);
    }

    public string GetCachePath(Uri url)
    {
        if (!IsAllowed(url)) throw new ArgumentException("Unsupported artwork URL.", nameof(url));
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url.AbsoluteUri)));
        return Path.Combine(directory, key + ".image");
    }

    public async Task<string> GetAsync(Uri url, CancellationToken token = default)
    {
        string path = GetCachePath(url);
        token.ThrowIfCancellationRequested();
        if (IsCached(path)) return path;
        // The filename is the SHA256, so its first byte gives a stable lock stripe.
        var stripe = stripes[Convert.ToByte(Path.GetFileName(path)[..2], 16) % stripes.Length];
        await stripe.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            if (IsCached(path)) return path;
            await CheckMissingAsync(path, token).ConfigureAwait(false);
            await downloads.WaitAsync(token).ConfigureAwait(false);
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(15));
                return await DownloadAsync(url, path, deadline.Token).ConfigureAwait(false);
            }
            finally { downloads.Release(); }
        }
        finally { stripe.Release(); }
    }

    private static bool IsCached(string path)
    {
        try { return new FileInfo(path).Length is > 0 and <= MaximumBytes; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private async Task<string> DownloadAsync(Uri url, string path, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("Vesper/0.1 (+https://github.com/Casper-003/Vesper)");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            await SaveMissingAsync(path, response.StatusCode, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        string? mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is not null && !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            && !mediaType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Artwork response is not an image.");
        if (response.Content.Headers.ContentLength is > MaximumBytes or 0)
            throw new InvalidDataException("Artwork is empty or too large.");
        Directory.CreateDirectory(directory);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                byte[] buffer = new byte[BufferSize];
                long total = 0;
                int count;
                while ((count = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                {
                    total += count;
                    if (total > MaximumBytes) throw new InvalidDataException("Artwork is too large.");
                    await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                }
                if (total == 0) throw new InvalidDataException("Artwork is empty.");
                await output.FlushAsync(token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
            DeleteBestEffort(path + ".missing");
            return path;
        }
        finally
        {
            DeleteBestEffort(temporary);
        }
    }

    private async Task CheckMissingAsync(string path, CancellationToken token)
    {
        string missing = path + ".missing";
        try
        {
            // A negative entry is a tiny status/timestamp record, never an image
            // response or another task retained in memory for every library URL.
            if (!File.Exists(missing)) return;
            if (new FileInfo(missing).Length is > 0 and <= 64)
            {
                string[] fields = (await File.ReadAllTextAsync(missing, token).ConfigureAwait(false)).Split('|');
                if (fields.Length == 2 && int.TryParse(fields[0], CultureInfo.InvariantCulture, out int status)
                    && status is 404 or 410 && long.TryParse(fields[1], CultureInfo.InvariantCulture, out long seconds)
                    && seconds >= DateTimeOffset.MinValue.ToUnixTimeSeconds() && seconds <= DateTimeOffset.MaxValue.ToUnixTimeSeconds())
                {
                    var checkedAt = DateTimeOffset.FromUnixTimeSeconds(seconds);
                    var now = clock.GetUtcNow();
                    if (checkedAt <= now && now - checkedAt < TimeSpan.FromDays(1))
                        throw new HttpRequestException("Artwork is not available (cached).", null, (HttpStatusCode)status);
                }
            }
            DeleteBestEffort(missing);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private async Task SaveMissingAsync(string path, HttpStatusCode status, CancellationToken token)
    {
        string missing = path + ".missing";
        string temporary = missing + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(directory);
            string value = ((int)status).ToString(CultureInfo.InvariantCulture) + "|"
                + clock.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            await File.WriteAllTextAsync(temporary, value, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, missing, overwrite: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        finally { DeleteBestEffort(temporary); }
    }

    private static void DeleteBestEffort(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
