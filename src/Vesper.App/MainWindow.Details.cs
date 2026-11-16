using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Vesper.Core;

namespace Vesper.App;

public sealed partial class MainWindow
{
    private static readonly HttpClient lookupHttp = new() { Timeout = TimeSpan.FromSeconds(15) };
    internal MainViewModel Model => viewModel;
    internal MusicBrainzClient SongLookup { get; } = new(lookupHttp);
    private readonly BilibiliVideoClient videoLookup = new(lookupHttp);
    private readonly LrcLibClient lyricsLookup = new(lookupHttp);
    private readonly SemaphoreSlim automaticLookupSlots = new(2, 2);
    private readonly Dictionary<string, Task> automaticLookups = [];
    private AutomaticSongLookup? automaticLookup;
    private AdditionalSongSources? additionalSources;
    private AdditionalSongSources Sources => additionalSources ??= new(lookupHttp, SongLookup, lyricsLookup);
    internal Task<IReadOnlyList<SongMatch>> SearchSongSourcesAsync(string title, CancellationToken token) => Sources.SearchMetadata(title, token);
    internal Task<SongMetadata> GetSongSourceMetadataAsync(SongMatch match, CancellationToken token) => Sources.GetMetadata(match, token);
    private AutomaticSongLookup AutomaticLookup => automaticLookup ??= new(Sources.SearchMetadata, Sources.GetMetadata,
        (title, token) => Sources.SearchLyrics(title, token), Sources.SearchLyricsWithArtist);
    private readonly HashSet<string> videoLookups = [];
    private readonly Dictionary<string, DateTimeOffset> videoCheckedAt = [];

    private void OnOpenSong(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not LibrarySongItem song) return;
        string key = "歌曲详情:" + song.Song.Id;
        if ((ContentFrame.Content as ShellPage)?.PageKey == key) return;
        NavigateShell(new ShellNavigationRequest(key, this, song.Song.Id));
    }

    internal async Task LoadDetailsAsync(LibrarySongItem song)
    {
        await LoadSongMetadataAsync(song);
        if (closed) return;
        song = viewModel.FindSong(song.Song.Id) ?? song;
        await LoadCoverAsync(song);
        await EnsureAutomaticLookupAsync(song);
        if (closed) return;
        song = viewModel.FindSong(song.Song.Id) ?? song;
        await LoadCoverAsync(song);
        await MatchVideoAsync(song);
    }

    private Task EnsureAutomaticLookupAsync(LibrarySongItem song)
    {
        string id = song.Song.Id;
        if (automaticLookups.TryGetValue(id, out var pending) && !pending.IsCompleted) return pending;
        return automaticLookups[id] = RunAutomaticLookupAsync(song);
    }

    private async Task RunAutomaticLookupAsync(LibrarySongItem song)
    {
        SongLookupInput? input = viewModel.GetLookupInput(song.Song.Id);
        if (closed || viewModel.ReadOnly || input is null
            || !input.ShouldLookup(DateTimeOffset.UtcNow)) return;
        bool entered = false;
        song.LookupPending = true;
        viewModel.BeginMetadataRead();
        try
        {
            await automaticLookupSlots.WaitAsync(metadataCancellation.Token);
            entered = true;
            for (int attempt = 0; attempt < 3 && !closed; attempt++)
            {
                input = viewModel.GetLookupInput(song.Song.Id);
                if (input is null || !input.ShouldLookup(DateTimeOffset.UtcNow)) break;
                var result = await AutomaticLookup.LookupAsync(input, metadataCancellation.Token);
                if (closed) break;
                if (viewModel.GetLookupInput(song.Song.Id)?.Key != input.Key) continue;
                await viewModel.SaveAutomaticLookupAsync(song.Song.Id, input, result);
                break;
            }
        }
        catch (OperationCanceledException) when (metadataCancellation.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!closed && input is not null)
                await viewModel.SaveAutomaticLookupAsync(song.Song.Id, input,
                    new(null, null, LookupOutcome.Unavailable, LookupOutcome.Unavailable));
        }
        finally
        {
            if (entered) automaticLookupSlots.Release();
            song.LookupPending = false;
            if (viewModel.FindSong(song.Song.Id) is { } canonical) canonical.LookupPending = false;
            viewModel.EndMetadataRead();
        }
    }

    internal async Task EnsurePlaybackLyricsAsync(Song song, CancellationToken token)
    {
        if (closed) return;
        var item = viewModel.FindSong(song.Id);
        if (item is not null && !item.MetadataLoaded)
            await LoadSongMetadataAsync(item);

        SongDetails details = viewModel.GetSongDetails(song.Id);
        if (details.LyricsManualOverride) return;

        ParsedLyrics current = LyricifyService.Parse(details, song.AvailablePath);
        if (current.HasWordSync) return;

        SongMetadata meta = details.Effective(song.Title);
        string fileName = Path.GetFileName(song.Sources.Concat(song.KnownSources ?? []).FirstOrDefault() ?? song.Title);
        var queries = SongNameMatching.Plan(fileName, meta.Title, meta.Artist);
        string searchTitle = queries.FirstOrDefault()?.Title ?? meta.Title;
        if (string.IsNullOrWhiteSpace(searchTitle)) return;

        var candidates = await LyricifyService.SearchCandidatesAsync(
            searchTitle, meta.Artist, meta.Album, meta.DurationSeconds, Sources, token);
        if (closed || candidates.Count == 0) return;

        var chosen = LrcLibClient.Choose(candidates, queries, meta.Artist, meta.Album, meta.DurationSeconds);
        if (chosen is null) return;

        var lyrics = LrcLibClient.ToLyrics(chosen, meta.DurationSeconds, fileName);
        if (!closed && !viewModel.ReadOnly)
        {
            await viewModel.SaveUpgradedLyricsAsync(song.Id, lyrics);
            RefreshSinging();
        }
    }

    internal async Task<bool> ShowLyricsManageDialogAsync(LibrarySongItem song, ElementTheme theme)
    {
        if (closed || dialogOpenLyrics || Root.XamlRoot is null) return false;
        dialogOpenLyrics = true;
        using var cancellation = new CancellationTokenSource();
        bool changed = false;

        string id = song.Song.Id;
        SongDetails details = viewModel.GetSongDetails(id);
        SongMetadata meta = details.Effective(song.Title);
        string fileName = Path.GetFileNameWithoutExtension(song.Song.Sources.Concat(song.Song.KnownSources).FirstOrDefault() ?? song.Song.Title);
        var hint = SongSearchHint.From(fileName, meta);

        var titleBox = new TextBox { Header = "歌名", Text = string.IsNullOrWhiteSpace(hint.Title) ? song.Title : hint.Title };
        var artistBox = new TextBox { Header = "艺人（可选）", Text = string.IsNullOrWhiteSpace(hint.Artist) ? meta.Artist : hint.Artist };
        var searchButton = new Button { Content = "搜索在线歌词", VerticalAlignment = VerticalAlignment.Bottom };
        var progress = new ProgressRing { Width = 20, Height = 20, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center };
        var statusText = new TextBlock { Text = "正在搜索多源逐字／逐行歌词…", TextWrapping = TextWrapping.Wrap, FontSize = 12 };

        var searchRow = new Grid { ColumnSpacing = 8 };
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        searchRow.Children.Add(titleBox);
        Grid.SetColumn(artistBox, 1); searchRow.Children.Add(artistBox);
        Grid.SetColumn(searchButton, 2); searchRow.Children.Add(searchButton);
        Grid.SetColumn(progress, 3); searchRow.Children.Add(progress);

        var results = new ListView
        {
            MaxHeight = 190,
            SelectionMode = ListViewSelectionMode.Single,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        results.ItemTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
              <StackPanel Spacing="2" Padding="6,6">
                <TextBlock Text="{Binding Headline}" FontWeight="SemiBold" TextWrapping="Wrap"/>
                <TextBlock Text="{Binding MetaLine}" FontSize="12" TextWrapping="Wrap"/>
                <TextBlock Text="{Binding PreviewLine}" FontSize="11" MaxLines="1" TextTrimming="CharacterEllipsis" Opacity="0.72"/>
              </StackPanel>
            </DataTemplate>
            """);

        int offsetMs = details.LyricsOffsetMs;
        var offsetReadout = new TextBlock
        {
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 64,
            TextAlignment = TextAlignment.Center
        };
        void RefreshOffsetText() => offsetReadout.Text = $"{(offsetMs >= 0 ? "+" : "")}{offsetMs / 1000.0:0.0}s";
        RefreshOffsetText();

        Button OffsetBtn(string label, int delta, bool reset = false)
        {
            var btn = new Button { Content = label, Padding = new Thickness(10, 4, 10, 4), FontSize = 12 };
            btn.Click += (_, _) =>
            {
                offsetMs = reset ? 0 : Math.Clamp(offsetMs + delta, -15000, 15000);
                RefreshOffsetText();
            };
            return btn;
        }

        var offsetBar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        offsetBar.Children.Add(new TextBlock { Text = "时间轴偏移：", VerticalAlignment = VerticalAlignment.Center, FontSize = 13 });
        offsetBar.Children.Add(OffsetBtn("-0.5s", -500));
        offsetBar.Children.Add(OffsetBtn("-0.1s", -100));
        offsetBar.Children.Add(offsetReadout);
        offsetBar.Children.Add(OffsetBtn("+0.1s", 100));
        offsetBar.Children.Add(OffsetBtn("+0.5s", 500));
        offsetBar.Children.Add(OffsetBtn("归零", 0, reset: true));

        string initialRaw = details.OnlineLyrics?.WordLyricsFile is { Length: > 0 } w ? w
            : details.OnlineLyrics?.SyncedLyrics is { Length: > 0 } s ? s
            : details.OnlineLyrics?.PlainText is { Length: > 0 } p ? p
            : details.Local?.Lyrics ?? "";
        var manualBox = new TextBox
        {
            Header = "手动编辑或粘贴歌词（支持 LRC / 逐字标签 / 纯文本）",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 140,
            Text = initialRaw
        };
        ScrollViewer.SetVerticalScrollBarVisibility(manualBox, ScrollBarVisibility.Auto);

        LyricsCandidate? selectedOnlineCandidate = null;
        bool manualEdited = false;
        bool programmaticTextUpdate = false;

        manualBox.TextChanged += (_, _) =>
        {
            if (!programmaticTextUpdate)
            {
                manualEdited = true;
                selectedOnlineCandidate = null;
            }
        };

        results.SelectionChanged += (_, _) =>
        {
            if (results.SelectedItem is not LyricsCandidateOption option) return;
            selectedOnlineCandidate = option.Candidate;
            manualEdited = false;
            programmaticTextUpdate = true;
            try
            {
                manualBox.Text = option.Candidate.WordLyricsFile.Length > 0 ? option.Candidate.WordLyricsFile
                    : option.Candidate.SyncedLyrics.Length > 0 ? option.Candidate.SyncedLyrics
                    : option.Candidate.PlainText;
            }
            finally { programmaticTextUpdate = false; }
        };

        var rootPanel = new StackPanel { Spacing = 12, MinWidth = 460, MaxWidth = 580 };
        foreach (UIElement el in new UIElement[] { searchRow, statusText, results, offsetBar, manualBox })
            rootPanel.Children.Add(el);

        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            RequestedTheme = theme,
            Title = "歌词匹配与调整",
            Content = new ScrollViewer { Content = rootPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            PrimaryButtonText = "应用",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };

        bool busy = false;
        async Task RunSearchAsync()
        {
            if (busy || string.IsNullOrWhiteSpace(titleBox.Text)) return;
            busy = true;
            searchButton.IsEnabled = titleBox.IsEnabled = artistBox.IsEnabled = false;
            progress.Visibility = Visibility.Visible; progress.IsActive = true;
            statusText.Text = "正在检索 QQ 音乐 / 网易云 / 酷狗 / LRCLIB…";
            results.ItemsSource = null;
            viewModel.BeginMetadataRead();
            try
            {
                var found = await LyricifyService.SearchCandidatesAsync(
                    titleBox.Text.Trim(), artistBox.Text.Trim(), meta.Album, meta.DurationSeconds, Sources, cancellation.Token);
                if (cancellation.IsCancellationRequested) return;

                var rows = found.Select(c => new LyricsCandidateOption(c)).ToArray();
                results.ItemsSource = rows;
                statusText.Text = rows.Length == 0
                    ? "未找到在线候选，可修改关键词重试，或在下方直接粘贴／修改歌词。"
                    : $"找到 {rows.Length} 个候选（点击可选中替换，直接点应用也会保存偏移）。";
            }
            catch (OperationCanceledException) { }
            catch
            {
                if (!cancellation.IsCancellationRequested)
                    statusText.Text = "在线检索暂不可用，请在下方手动修改歌词或仅调整偏移。";
            }
            finally
            {
                busy = false;
                searchButton.IsEnabled = titleBox.IsEnabled = artistBox.IsEnabled = true;
                progress.IsActive = false; progress.Visibility = Visibility.Collapsed;
                viewModel.EndMetadataRead();
            }
        }

        searchButton.Click += async (_, _) => await RunSearchAsync();

        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                if (selectedOnlineCandidate is { } picked)
                {
                    var lyrics = LrcLibClient.ToLyrics(picked, meta.DurationSeconds, fileName);
                    await viewModel.SaveManualLyricsAsync(id, lyrics, offsetMs);
                    changed = true;
                }
                else if (manualEdited)
                {
                    string edited = manualBox.Text.Trim();
                    bool hasWord = LyricifyService.ContainsWordTimings(edited);
                    string plain = LyricifyService.ExtractPlainText(edited);
                    var parsed = LyricifyService.ParseRaw(edited);
                    string synced = parsed.HasLineSync ? (hasWord ? LyricifyService.TryGenerateLrc(edited) : edited) : "";
                    var manualLyrics = new SongLyrics(
                        plain, synced, hasWord ? edited : "", hasWord,
                        "", meta.Title, meta.Artist, meta.DurationSeconds, TimingCompatible: true)
                    { RawFormat = parsed.SourceFormat };
                    await viewModel.SaveManualLyricsAsync(id, manualLyrics, offsetMs);
                    changed = true;
                }
                else if (offsetMs != details.LyricsOffsetMs)
                {
                    await viewModel.SaveLyricsOffsetAsync(id, offsetMs);
                    changed = true;
                }
            }
            finally { deferral.Complete(); }
        };

        dialog.Closing += (_, _) => cancellation.Cancel();
        dialog.Opened += (_, _) => _ = RunSearchAsync();

        try { await dialog.ShowAsync(); }
        finally
        {
            cancellation.Cancel();
            dialogOpenLyrics = false;
        }
        return changed;
    }

    private bool dialogOpenLyrics;

    public sealed class LyricsCandidateOption(LyricsCandidate candidate)
    {
        public LyricsCandidate Candidate { get; } = candidate;
        public string Headline => string.IsNullOrWhiteSpace(Candidate.Artist)
            ? Candidate.Title
            : $"{Candidate.Title} - {Candidate.Artist}";
        public string MetaLine
        {
            get
            {
                string source = Candidate.SourceUrl.Contains("y.qq.com", StringComparison.OrdinalIgnoreCase) ? "QQ 音乐"
                    : Candidate.SourceUrl.Contains("music.163.com", StringComparison.OrdinalIgnoreCase) ? "网易云"
                    : Candidate.SourceUrl.Contains("kugou.com", StringComparison.OrdinalIgnoreCase) ? "酷狗"
                    : "LRCLIB";
                string syncType = Candidate.HasWordSync ? "逐字歌词"
                    : !string.IsNullOrWhiteSpace(Candidate.SyncedLyrics) ? "逐行歌词"
                    : "纯文本";
                string dur = Candidate.DurationSeconds > 0
                    ? TimeSpan.FromSeconds(Candidate.DurationSeconds).ToString(@"m\:ss")
                    : "--:--";
                string trans = string.IsNullOrWhiteSpace(Candidate.TranslationLyrics) ? "" : " · 含翻译";
                return $"{source} · {syncType} · {dur}{trans}";
            }
        }
        public string PreviewLine => (Candidate.PlainText.Length > 0 ? Candidate.PlainText : LyricifyService.ExtractPlainText(Candidate.SyncedLyrics))
            .Replace("\r\n", " ").Replace('\n', ' ').Trim();
    }

    private async Task MatchVideoAsync(LibrarySongItem song)
    {
        string? path = song.Song.AvailablePath;
        if (closed || path is null || IsVideoFile(path) || song.HasMatchedVideo || !song.HasArtist || song.DurationSeconds <= 0) return;
        string id = song.Song.Id, title = song.Title, artist = song.ArtistDetail;
        string identity = $"{id}\n{title}\n{artist}\n{song.DurationSeconds}";
        if (videoLookups.Contains(identity) || videoCheckedAt.TryGetValue(identity, out var checkedAt)
            && DateTimeOffset.UtcNow - checkedAt < TimeSpan.FromMinutes(5)) return;
        videoLookups.Add(identity);
        viewModel.BeginMetadataRead();
        try
        {
            var candidates = await videoLookup.SearchAsync(title, artist, metadataCancellation.Token);
            var match = BilibiliVideoClient.ChooseReliable(candidates, title, artist, song.DurationSeconds,
                Path.GetFileNameWithoutExtension(path));
            videoCheckedAt[identity] = DateTimeOffset.UtcNow;
            var current = viewModel.FindSong(id);
            if (!closed && match is not null && current?.Title == title && current.ArtistDetail == artist)
                await viewModel.SaveVideoMatchAsync(id, match, title, artist);
        }
        catch (Exception) { /* 检索失败不打断本地浏览；不要求用户检查候选。 */ }
        finally { videoLookups.Remove(identity); viewModel.EndMetadataRead(); }
    }
}
