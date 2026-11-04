using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Vesper.Core;

namespace Vesper.App;

public sealed partial class SongDetailsView : UserControl
{
    private readonly MainWindow host;
    private readonly string songId;
    private bool dialogOpen;
    private LibrarySongItem? observedSong;
    private SongEntityLinkState? renderedEntityLinks;
    private readonly Dictionary<Hyperlink, (string Id, bool Album)> entityLinkTargets = [];
    private bool entityRefreshQueued;
    private long entityRefreshGeneration;
    private LibrarySongItem? Song => DataContext as LibrarySongItem;

    public SongDetailsView(MainWindow host, string songId)
    {
        this.host = host; this.songId = songId;
        InitializeComponent();
        RefreshSong();
    }

    private void OnDetailsSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // 左侧留白随页面扩展，封面适度放大，窄窗口仍为资料与歌词保留空间。
        double leftWidth = DetailsLayout.ActualWidth / 2;
        double size = Math.Max(96, Math.Min(560, Math.Min(leftWidth - 64, DetailsLayout.ActualHeight * .68)));
        CoverPanel.Width = size;
        CoverArt.Width = CoverArt.Height = size;
        CoverActionsOffset.Y = size / 2 + 44;
    }

    private void RefreshSong()
    {
        var current = host.Model.FindSong(songId);
        if (IsLoaded && !ReferenceEquals(observedSong, current))
        {
            if (observedSong is not null) observedSong.PropertyChanged -= OnSongChanged;
            observedSong = current;
            if (observedSong is not null) observedSong.PropertyChanged += OnSongChanged;
        }
        DataContext = current;
        EditButton.IsEnabled = SearchButton.IsEnabled = !host.Model.ReadOnly && Song is not null;
        RefreshEntityLinks();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        host.Model.PropertyChanged -= OnLibraryChanged;
        host.Model.PropertyChanged += OnLibraryChanged;
        host.Model.SearchIndexChanged -= OnCatalogChanged;
        host.Model.SearchIndexChanged += OnCatalogChanged;
        RefreshSong();
        if (Song is { } song) await host.LoadDetailsAsync(song);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        host.Model.PropertyChanged -= OnLibraryChanged;
        host.Model.SearchIndexChanged -= OnCatalogChanged;
        if (observedSong is not null) observedSong.PropertyChanged -= OnSongChanged;
        observedSong = null;
        entityRefreshGeneration++;
        entityRefreshQueued = false;
    }
    private void OnLibraryChanged(object? sender, PropertyChangedEventArgs e)
    { if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(MainViewModel.LibrarySummary)) QueueEntityRefresh(); }
    private void OnCatalogChanged(object? sender, EventArgs e) => QueueEntityRefresh();
    private void OnSongChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName is nameof(LibrarySongItem.Title) or nameof(LibrarySongItem.ArtistDetail)
            or nameof(LibrarySongItem.Album) or nameof(LibrarySongItem.HasArtist) or nameof(LibrarySongItem.HasAlbum)) QueueEntityRefresh();
    }
    private void QueueEntityRefresh()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(QueueEntityRefresh);
            return;
        }
        if (entityRefreshQueued) return;
        entityRefreshQueued = true;
        long generation = entityRefreshGeneration;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (generation != entityRefreshGeneration) return;
            entityRefreshQueued = false;
            if (IsLoaded) RefreshSong();
        });
    }

    private void RefreshEntityLinks()
    {
        var links = host.GetSongEntityLinks(songId);
        if (SameEntityLinks(renderedEntityLinks, links)) return;
        (string Id, bool Album)? focusedTarget = null;
        if (XamlRoot is not null && FocusManager.GetFocusedElement(XamlRoot) is Hyperlink focused
            && entityLinkTargets.TryGetValue(focused, out var target)) focusedTarget = target;
        entityLinkTargets.Clear();
        ArtistValue.Inlines.Clear(); AlbumValue.Inlines.Clear();
        renderedEntityLinks = links;
        AppendArtistLinks(links);
        if (links.Album is { } album) AlbumValue.Inlines.Add(CreateEntityLink(album, links.AlbumText, true));
        else AlbumValue.Inlines.Add(new Run { Text = links.AlbumText });
        if (focusedTarget is { } previous)
        {
            var replacement = entityLinkTargets.FirstOrDefault(pair => pair.Value == previous).Key
                ?? entityLinkTargets.Keys.FirstOrDefault();
            if (replacement is not null) replacement.Focus(FocusState.Programmatic);
            else if (IsLoaded && EditButton.IsEnabled) EditButton.Focus(FocusState.Programmatic);
            else if (IsLoaded) DetailsScroll.Focus(FocusState.Programmatic);
        }
    }

    private void AppendArtistLinks(SongEntityLinkState links)
    {
        if (links.Artists.Count == 0) { ArtistValue.Inlines.Add(new Run { Text = links.ArtistText }); return; }
        var spans = new List<(int Start, int Length, SongEntityLinkTarget Target)>();
        // Preserve original credited spelling and join phrases whenever each
        // catalog performer can be located without overlapping another credit.
        foreach (var artist in links.Artists.OrderByDescending(artist => artist.Aliases.Append(artist.Name).Max(name => name.Length)))
        {
            (int Start, int Length)? found = null;
            foreach (string name in artist.Aliases.Append(artist.Name).Where(name => name.Length > 0).Distinct().OrderByDescending(name => name.Length))
            {
                int start = 0;
                while (start < links.ArtistText.Length)
                {
                    int index = links.ArtistText.IndexOf(name, start, StringComparison.OrdinalIgnoreCase);
                    if (index < 0) break;
                    if (!spans.Any(span => index < span.Start + span.Length && index + name.Length > span.Start))
                    { found = (index, name.Length); break; }
                    start = index + name.Length;
                }
                if (found is not null) break;
            }
            if (found is { } match) spans.Add((match.Start, match.Length, artist));
        }
        if (spans.Count == links.Artists.Count)
        {
            int start = 0;
            foreach (var span in spans.OrderBy(span => span.Start))
            {
                if (span.Start > start) ArtistValue.Inlines.Add(new Run { Text = links.ArtistText[start..span.Start] });
                ArtistValue.Inlines.Add(CreateEntityLink(span.Target, links.ArtistText.Substring(span.Start, span.Length), false));
                start = span.Start + span.Length;
            }
            if (start < links.ArtistText.Length) ArtistValue.Inlines.Add(new Run { Text = links.ArtistText[start..] });
        }
        else
        {
            // Structured credits remain separately navigable even when their
            // authoritative names cannot safely be mapped into legacy text.
            foreach (var artist in links.Artists)
            {
                if (ArtistValue.Inlines.Count > 0) ArtistValue.Inlines.Add(new Run { Text = " / " });
                ArtistValue.Inlines.Add(CreateEntityLink(artist, artist.Name, false));
            }
        }
    }

    private Hyperlink CreateEntityLink(SongEntityLinkTarget target, string text, bool album)
    {
        var link = new Hyperlink { IsTabStop = true };
        link.Inlines.Add(new Run { Text = text });
        AutomationProperties.SetName(link, text);
        AutomationProperties.SetHelpText(link, album ? "查看专辑详情" : "查看艺人详情");
        entityLinkTargets.Add(link, (target.Id, album));
        link.Click += (_, _) =>
        {
            if (album) host.OpenSongAlbum(songId, target.Id);
            else host.OpenSongArtist(songId, target.Id);
            if (IsLoaded) RefreshSong();
        };
        return link;
    }

    private static bool SameEntityLinks(SongEntityLinkState? before, SongEntityLinkState after) => before is not null
        && before.SongId == after.SongId && before.ArtistText == after.ArtistText && before.AlbumText == after.AlbumText
        && SameTarget(before.Album, after.Album) && before.Artists.Count == after.Artists.Count
        && before.Artists.Zip(after.Artists).All(pair => SameTarget(pair.First, pair.Second));
    private static bool SameTarget(SongEntityLinkTarget? before, SongEntityLinkTarget? after) => before is null ? after is null
        : after is not null && before.Id == after.Id && before.Name == after.Name && before.Aliases.SequenceEqual(after.Aliases);
    private async void OnFavorite(object sender, RoutedEventArgs e)
    { if (Song is { } song) await host.Model.ToggleFavoriteAsync(song); }
    private async void OnQueue(object sender, RoutedEventArgs e)
    { if (Song is { } song) await host.QueueSongAsync(song, sender as FrameworkElement); }

    private async void OnEdit(object sender, RoutedEventArgs e)
    {
        if (dialogOpen || Song is not { } song) return;
        dialogOpen = true;
        var title = new TextBox { Header = "歌名", Text = song.Title };
        var artist = new TextBox { Header = "艺人", Text = song.ArtistDetail };
        var album = new TextBox { Header = "专辑", Text = song.Album };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        var content = new StackPanel { Spacing = 14, MinWidth = 320 };
        foreach (var field in new UIElement[] { title, artist, album,
            new TextBlock { Text = "修改保存在曲库中，原文件保持不变。", TextWrapping = TextWrapping.Wrap }, error }) content.Children.Add(field);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, RequestedTheme = ActualTheme, Title = "修改歌曲资料", Content = content,
            PrimaryButtonText = "保存", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary
        };
        title.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(title.Text);
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                args.Cancel = !await host.Model.SaveCorrectionsAsync(songId, title.Text, artist.Text, album.Text);
                if (args.Cancel) { error.Text = host.Model.Problem; error.Visibility = Visibility.Visible; }
            }
            finally { deferral.Complete(); }
        };
        try { await dialog.ShowAsync(); }
        finally { dialogOpen = false; }
        if (Song is { } current) await host.LoadDetailsAsync(current);
    }

    private async void OnSearch(object sender, RoutedEventArgs e)
    {
        if (dialogOpen || Song is not { } song) return;
        dialogOpen = true;
        using var cancellation = new CancellationTokenSource();
        string fileName = Path.GetFileNameWithoutExtension(song.Song.Sources.Concat(song.Song.KnownSources).FirstOrDefault() ?? song.Song.Title);
        var hint = SongSearchHint.From(fileName, host.Model.GetSongMetadata(songId));
        var title = new TextBox { Header = "歌名", Text = hint.Title };
        var artist = new TextBox { Header = "艺人（可选）", Text = hint.Artist };
        var search = new Button { Content = "检索", HorizontalAlignment = HorizontalAlignment.Right };
        var status = new TextBlock { Text = "核对检索词后开始检索。", TextWrapping = TextWrapping.Wrap };
        var progress = new ProgressRing { Width = 20, Height = 20, Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Left };
        var results = new ListView { MaxHeight = 240, SelectionMode = ListViewSelectionMode.Single,
            HorizontalContentAlignment = HorizontalAlignment.Stretch };
        results.ItemTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
              <StackPanel Spacing="4" Padding="4,8">
                <TextBlock Text="{Binding Title}" FontWeight="SemiBold" TextWrapping="Wrap"/>
                <TextBlock Text="{Binding Artist}" TextWrapping="Wrap"/>
                <TextBlock Text="{Binding Description}" FontSize="12" TextWrapping="Wrap"/>
              </StackPanel>
            </DataTemplate>
            """);
        var content = new StackPanel { Spacing = 12, MinWidth = 360, MaxWidth = 480 };
        foreach (var element in new UIElement[] { title, artist,
            new TextBlock { Text = "文件名：" + fileName, TextWrapping = TextWrapping.Wrap, FontSize = 12 }, search, progress, status, results }) content.Children.Add(element);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, RequestedTheme = ActualTheme, Title = "检索歌曲资料", Content = new ScrollViewer { Content = content },
            PrimaryButtonText = "采用资料", CloseButtonText = "取消", IsPrimaryButtonEnabled = false
        };
        bool busy = false;
        void ClearResults(object? _, TextChangedEventArgs args)
        { results.ItemsSource = null; dialog.IsPrimaryButtonEnabled = false; }
        title.TextChanged += ClearResults; artist.TextChanged += ClearResults;
        results.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = !busy && results.SelectedItem is SongMatch;
        search.Click += async (_, _) =>
        {
            if (busy || string.IsNullOrWhiteSpace(title.Text)) return;
            busy = true; search.IsEnabled = title.IsEnabled = artist.IsEnabled = false;
            dialog.IsPrimaryButtonEnabled = false; results.ItemsSource = null;
            progress.Visibility = Visibility.Visible; progress.IsActive = true;
            status.Text = "正在检索歌曲资料…";
            host.Model.BeginMetadataRead();
            try
            {
                var matches = await host.SearchSongSourcesAsync(title.Text, cancellation.Token);
                if (!string.IsNullOrWhiteSpace(artist.Text)) matches = matches.Where(match => SongNameMatching.SameArtist(match.Artist, artist.Text)).ToList();
                if (cancellation.IsCancellationRequested) return;
                results.ItemsSource = matches;
                status.Text = matches.Count == 0 ? "无匹配。可调整歌名或艺人后重试。" : "选择对应的歌曲和专辑。手动修正的字段会继续保留。";
            }
            catch (OperationCanceledException) { }
            catch (Exception) { if (!cancellation.IsCancellationRequested) status.Text = "暂时无法检索，请检查网络或稍后重试。本地资料仍可使用。"; }
            finally
            {
                busy = false; search.IsEnabled = title.IsEnabled = artist.IsEnabled = true;
                progress.IsActive = false; progress.Visibility = Visibility.Collapsed;
                host.Model.EndMetadataRead();
            }
        };
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            if (results.SelectedItem is not SongMatch match) { args.Cancel = true; return; }
            var deferral = args.GetDeferral();
            busy = true; search.IsEnabled = title.IsEnabled = artist.IsEnabled = results.IsEnabled = false;
            host.Model.BeginMetadataRead();
            try
            {
                var metadata = await host.GetSongSourceMetadataAsync(match, cancellation.Token);
                args.Cancel = !await host.Model.SaveOnlineMetadataAsync(songId, metadata);
                if (args.Cancel) status.Text = host.Model.Problem;
            }
            catch (Exception) { args.Cancel = true; status.Text = "暂时无法获取资料，请稍后重试。原资料已保留。"; }
            finally
            {
                busy = false; search.IsEnabled = title.IsEnabled = artist.IsEnabled = results.IsEnabled = true;
                host.Model.EndMetadataRead(); deferral.Complete();
            }
        };
        dialog.Closing += (_, _) => cancellation.Cancel();
        try { await dialog.ShowAsync(); }
        finally { cancellation.Cancel(); dialogOpen = false; }
        if (Song is { } current) await host.LoadDetailsAsync(current);
    }
}
