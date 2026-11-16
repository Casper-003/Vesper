using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Vesper.Core;
using Windows.UI;

namespace Vesper.App;

/// <summary>
/// Folia 原生 WebGL / Canvas 歌词视觉舞台宿主：
/// 底层使用原生流体取色背景做零闪烁过渡，上层通过 WebView2 虚拟主机加载 Folia 真实视觉引擎（17 种歌词模式与多种着色器背景）。
/// </summary>
internal sealed class FoliaLyricsStage : Grid, IDisposable
{
    private const string VirtualHostName = "folia.vesper.local";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private sealed record HostSyllableDto(int StartMs, int DurationMs, string Text);

    private sealed record HostLineDto(
        int StartMs,
        int EndMs,
        string Text,
        string? Translation,
        bool IsWordSynced,
        IReadOnlyList<HostSyllableDto> Syllables);

    private sealed record HostSongDto(
        string SongKey,
        string Title,
        string Artist,
        string Album,
        string? CoverUrl,
        string[] PaletteHex,
        double DurationSeconds,
        int OffsetMs,
        bool LoadingLyrics,
        bool CanSeek,
        string? RawLyrics,
        string? TranslationLyrics,
        string? RawFormat,
        IReadOnlyList<HostLineDto> FallbackLines);

    private sealed record HostClockDto(double PositionMs, bool Paused, double DurationSeconds);

    private sealed record HostEnvelope<T>(string Type, T Payload);

    private readonly FoliaFluidBackground underlay = new();
    private readonly WebView2 webView = new()
    {
        DefaultBackgroundColor = Color.FromArgb(255, 8, 9, 13),
        Opacity = 0,
        IsHitTestVisible = false
    };

    private Task? initTask;
    private bool pageReady;
    private bool disposed;
    private bool active;
    private bool chromeVisible;
    private uint? swatchArgb;
    private string? lastSentSongKey;
    private string? lastPostedSongJson;
    private HostSongDto? pendingSong;
    private double lastPositionMs;
    private double lastDurationSeconds;
    private bool lastPaused = true;

    public event Action<TimeSpan>? SeekRequested;
    public event Action? ManageLyricsRequested;
    public event Action? Interacted;

    public FoliaLyricsStage()
    {
        Children.Add(underlay);
        Children.Add(webView);

        Loaded += (_, _) =>
        {
            underlay.SetPresentation(swatchArgb, active);
            if (active) _ = EnsureWebViewInitializedAsync();
        };
        Unloaded += (_, _) => underlay.SetPresentation(swatchArgb, false);
    }

    internal void SetActive(bool value)
    {
        if (disposed) return;
        active = value;
        underlay.SetPresentation(swatchArgb, value);
        PostEnvelope("active", new { active = value });
        if (value) _ = EnsureWebViewInitializedAsync();
    }

    internal void SetCanSeek(bool canSeek)
    {
        if (disposed || pendingSong is null) return;
        if (pendingSong.CanSeek == canSeek) return;
        pendingSong = pendingSong with { CanSeek = canSeek };
        PostSongIfChanged(force: true);
    }

    internal void SetChromeVisible(bool visible)
    {
        if (disposed || chromeVisible == visible) return;
        chromeVisible = visible;
        PostEnvelope("chrome", new { controlsVisible = visible });
    }

    internal void SetSong(
        string songKey,
        string title,
        string artist,
        string album,
        string? coverUrl,
        uint? dominantArgb,
        string? rawLyrics,
        string? translationLyrics,
        string? rawFormat,
        ParsedLyrics lyrics,
        int lyricsOffsetMs,
        bool loading,
        bool canSeek,
        double durationSeconds)
    {
        if (disposed) return;

        swatchArgb = dominantArgb;
        underlay.SetPresentation(dominantArgb, active);

        var palette = CoverPalette.CreateFluidPalette(dominantArgb);
        string[] paletteHex = dominantArgb is { } argb && (argb >> 24) >= 64
            ? [ToHex(argb), ToHex(palette.PrimaryOrb), ToHex(palette.AccentOrb), ToHex(palette.SecondaryOrb)]
            : [ToHex(palette.PrimaryOrb), ToHex(palette.AccentOrb), ToHex(palette.SecondaryOrb)];

        var fallbackLines = lyrics.Lines.Select(line => new HostLineDto(
            line.StartMs,
            line.EndMs,
            line.Text,
            line.Translation,
            line.IsWordSynced,
            line.Syllables.Select(s => new HostSyllableDto(s.StartMs, s.DurationMs, s.Text)).ToArray()
        )).ToArray();

        pendingSong = new HostSongDto(
            songKey,
            title,
            artist,
            album,
            string.IsNullOrWhiteSpace(coverUrl) ? null : coverUrl,
            paletteHex,
            durationSeconds,
            lyricsOffsetMs,
            loading,
            canSeek,
            rawLyrics,
            translationLyrics,
            rawFormat,
            fallbackLines);

        if (active) _ = EnsureWebViewInitializedAsync();
        PostSongIfChanged(force: songKey != lastSentSongKey);
    }

    internal void UpdatePlaybackClock(TimeSpan position, TimeSpan duration, bool paused)
    {
        if (disposed) return;
        lastPositionMs = Math.Max(0, position.TotalMilliseconds);
        lastDurationSeconds = Math.Max(0, duration.TotalSeconds);
        lastPaused = paused;
        PostEnvelope("clock", new HostClockDto(lastPositionMs, lastPaused, lastDurationSeconds));
    }

    private async Task EnsureWebViewInitializedAsync()
    {
        if (disposed) return;
        initTask ??= InitializeCoreAsync();
        try
        {
            await initTask;
        }
        catch
        {
            // Keep fluid underlay visible if WebView2 runtime is unavailable or fails.
            initTask = null;
        }
    }

    private async Task InitializeCoreAsync()
    {
        if (disposed) return;

        string userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Vesper",
            "WebView2");
        Directory.CreateDirectory(userDataFolder);
        Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", userDataFolder);

        await webView.EnsureCoreWebView2Async();
        if (disposed || webView.CoreWebView2 is null) return;

        var core = webView.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.IsZoomControlEnabled = false;

        string stageDir = Path.Combine(AppContext.BaseDirectory, "Assets", "FoliaStage");
        if (!Directory.Exists(stageDir)) return;

        core.SetVirtualHostNameToFolderMapping(
            VirtualHostName,
            stageDir,
            CoreWebView2HostResourceAccessKind.Allow);

        core.WebMessageReceived += OnWebMessageReceived;
        core.Navigate($"https://{VirtualHostName}/vesper-stage.html");
    }

    private void OnWebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (disposed) return;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(args.WebMessageAsJson);
            if (!doc.RootElement.TryGetProperty("type", out var typeProp)) return;
            string? type = typeProp.GetString();
            switch (type)
            {
                case "ready":
                    pageReady = true;
                    webView.Opacity = 1;
                    webView.IsHitTestVisible = true;
                    PostEnvelope("active", new { active });
                    PostEnvelope("chrome", new { controlsVisible = chromeVisible });
                    PostSongIfChanged(force: true);
                    PostEnvelope("clock", new HostClockDto(lastPositionMs, lastPaused, lastDurationSeconds));
                    break;
                case "seek":
                    if (doc.RootElement.TryGetProperty("timeSeconds", out var secProp)
                        && secProp.TryGetDouble(out double sec)
                        && double.IsFinite(sec))
                    {
                        SeekRequested?.Invoke(TimeSpan.FromSeconds(Math.Max(0, sec)));
                    }
                    break;
                case "manage-lyrics":
                    ManageLyricsRequested?.Invoke();
                    break;
                case "interact":
                    Interacted?.Invoke();
                    break;
            }
        }
        catch
        {
            // Ignore malformed messages from embedded page.
        }
    }

    private void PostSongIfChanged(bool force)
    {
        if (disposed || !pageReady || webView.CoreWebView2 is null || pendingSong is null) return;
        string json = JsonSerializer.Serialize(new HostEnvelope<HostSongDto>("song", pendingSong), JsonOptions);
        if (!force && string.Equals(json, lastPostedSongJson, StringComparison.Ordinal)) return;
        lastSentSongKey = pendingSong.SongKey;
        lastPostedSongJson = json;
        webView.CoreWebView2.PostWebMessageAsJson(json);
    }

    private void PostEnvelope<T>(string type, T payload)
    {
        if (disposed || !pageReady || webView.CoreWebView2 is null) return;
        string json = JsonSerializer.Serialize(new HostEnvelope<T>(type, payload), JsonOptions);
        webView.CoreWebView2.PostWebMessageAsJson(json);
    }

    private static string ToHex(uint argb)
        => $"#{(byte)(argb >> 16):X2}{(byte)(argb >> 8):X2}{(byte)argb:X2}";

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        pageReady = false;
        if (webView.CoreWebView2 is { } core)
            core.WebMessageReceived -= OnWebMessageReceived;
        try { webView.Close(); }
        catch { /* Ignore shutdown errors on uninitialized WebView2. */ }
    }
}
