using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Vesper.Core;

namespace Vesper.App;

public sealed partial class MainWindow
{
    private readonly SearchRequestGate searchRequests = new();
    private SearchRequest? pendingSearch;
    private string searchText = "";
    private string? scheduledSearch;
    private SearchResultsView? searchResults;
    private TextBox? searchTextBox;
    private bool composingSearch, restoringSearchText, publishingSearch, deferredSearchRefresh;

    private void InitializeSearchPresentation()
    {
        GlobalSearchBox.TextChanged += OnGlobalSearchTextChanged;
        GlobalSearchBox.Loaded += (_, _) => AttachSearchInput();
        viewModel.SearchIndexChanged += OnSearchIndexChanged;
        ContentFrame.Navigating += (_, _) =>
        {
            if ((ContentFrame.Content as ShellPage)?.PageKey == "搜索") searchResults?.CapturePosition();
        };
    }

    private void AttachSearchInput()
    {
        var textBox = FindSearchPart<TextBox>(GlobalSearchBox);
        if (textBox is null || ReferenceEquals(textBox, searchTextBox)) return;
        if (searchTextBox is not null)
        {
            searchTextBox.TextCompositionStarted -= OnSearchCompositionStarted;
            searchTextBox.TextCompositionEnded -= OnSearchCompositionEnded;
        }
        searchTextBox = textBox;
        textBox.TextCompositionStarted += OnSearchCompositionStarted;
        textBox.TextCompositionEnded += OnSearchCompositionEnded;
    }

    private void OnSearchCompositionStarted(TextBox sender, TextCompositionStartedEventArgs args)
    {
        composingSearch = true;
        pendingSearch = searchRequests.BeginEdit();
        scheduledSearch = null;
    }
    private void OnSearchCompositionEnded(TextBox sender, TextCompositionEndedEventArgs args)
    {
        composingSearch = false;
        var request = pendingSearch;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!closed && !composingSearch && request is not null && searchRequests.CanPublish(request))
                ScheduleSearch(GlobalSearchBox.Text);
        });
    }
    private void OnGlobalSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (!ready || closed || restoringSearchText || args.Reason == AutoSuggestionBoxTextChangeReason.ProgrammaticChange) return;
        if (composingSearch) { pendingSearch = searchRequests.BeginEdit(); scheduledSearch = null; return; }
        ScheduleSearch(sender.Text);
    }

    private SearchResultsView SearchResults => searchResults ??= CreateSearchResults();
    private SearchResultsView CreateSearchResults()
    {
        var result = new SearchResultsView(this);
        result.InteractionEnded = () =>
        {
            if (!deferredSearchRefresh || closed || result.IsInteracting) return;
            deferredSearchRefresh = false;
            var request = pendingSearch;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!closed && request is not null && searchRequests.CanPublish(request)
                    && (ContentFrame.Content as ShellPage)?.PageKey == "搜索")
                    ScheduleSearch(searchText, true, true);
            });
        };
        return result;
    }

    private void ScheduleSearch(string text, bool force = false, bool restore = false)
    {
        if (closed || composingSearch) return;
        if (!force && scheduledSearch == text && pendingSearch is { } previous && searchRequests.CanPublish(previous)) return;
        searchText = text; scheduledSearch = text;
        var request = searchRequests.BeginEdit(); pendingSearch = request;
        deferredSearchRefresh = false;
        if (string.IsNullOrWhiteSpace(text))
        {
            searchResults?.Clear(text);
            return;
        }
        _ = RunSearchAsync(text, request, restore);
    }

    private async Task RunSearchAsync(string query, SearchRequest request, bool restore)
    {
        try
        {
            await Task.Delay(SearchOptions.Debounce, request.CancellationToken);
            if (!searchRequests.CanPublish(request)) return;
            if (viewModel.SearchIndex is null)
            {
                EnsureSearchPage();
                SearchResults.ShowStatus("正在准备搜索…");
                await viewModel.EnsureSearchIndexAsync();
            }
            if (!searchRequests.CanPublish(request)) return;
            var index = viewModel.SearchIndex;
            if (index is null)
            {
                EnsureSearchPage(); SearchResults.ShowStatus("搜索暂不可用，请重试。", true); return;
            }
            int count = restore ? Math.Max(30, SearchResults.LoadedSongCount) : 30;
            var result = await Task.Run(() => index.Search(query, request.QueryId, new SearchOptions(SongPageSize: count), request.CancellationToken), request.CancellationToken);
            // Await resumes on the UI dispatcher. Check authority here, at the actual write.
            if (!searchRequests.CanPublish(request)) return;
            if (viewModel.IndexRevision != index.Revision)
            { ScheduleSearch(query, true, restore); return; }
            if (restore && SearchResults.IsInteracting) { deferredSearchRefresh = true; return; }
            EnsureSearchPage();
            SearchResults.Apply(result, restore);
            if (viewModel.SearchIndexProblem.Length > 0) SearchResults.ShowStatus("资料更新未完成，当前保留上次结果。", true);
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (closed || !searchRequests.CanPublish(request)) return;
            EnsureSearchPage(); SearchResults.ShowStatus("搜索暂不可用，请重试。", true);
        }
    }

    private void EnsureSearchPage()
    {
        if ((ContentFrame.Content as ShellPage)?.PageKey == "搜索") return;
        publishingSearch = true;
        try { ContentFrame.Navigate(typeof(ShellPage), new ShellNavigationRequest("搜索", this), new EntranceNavigationTransitionInfo()); }
        finally { publishingSearch = false; }
    }

    private void OnSearchPageNavigated(string key)
    {
        if (key != "搜索")
        {
            searchRequests.Leave(); scheduledSearch = null;
            return;
        }
        if (publishingSearch) return;
        restoringSearchText = true;
        try { GlobalSearchBox.Text = searchText; }
        finally { restoringSearchText = false; }
        if (string.IsNullOrWhiteSpace(searchText)) { SearchResults.Clear(); return; }
        if (SearchResults.Revision == viewModel.IndexRevision && SearchResults.Query == searchText)
            _ = SearchResults.RestorePositionAsync();
        else ScheduleSearch(searchText, true, true);
    }

    private void OnSearchIndexChanged(object? sender, EventArgs args) => DispatcherQueue.TryEnqueue(() =>
    {
        if (closed) return;
        RequestLibraryActivityStatus();
        if (composingSearch || (ContentFrame.Content as ShellPage)?.PageKey != "搜索" || string.IsNullOrWhiteSpace(searchText)) return;
        if (SearchResults.IsInteracting) { deferredSearchRefresh = true; return; }
        // Building notifications do not cancel a usable old snapshot query. A new publication does.
        if (viewModel.SearchIndex is { } index && index.Revision != SearchResults.Revision)
            ScheduleSearch(searchText, true, true);
        else if (viewModel.SearchIndexProblem.Length > 0) SearchResults.ShowStatus("资料更新未完成，请重试。", true);
    });

    private void RequestLibraryActivityStatus()
    {
        bool indexing = viewModel.IsSearchIndexBuilding;
        bool foregroundActivity = viewModel.HasBackgroundActivity;
        activityDisplay.Request(new(foregroundActivity || indexing,
            foregroundActivity || !indexing ? viewModel.ActivityLabel : "正在建立搜索索引",
            foregroundActivity || !indexing ? viewModel.ActivityTooltip : "正在更新本地搜索资料，可继续浏览和点歌。",
            viewModel.ActivityGlyph), activityClock.Elapsed);
    }

    internal void RetrySearch()
    {
        _ = viewModel.EnsureSearchIndexAsync();
        ScheduleSearch(searchText, true, true);
    }
    internal void OpenSearchSong(string songId)
    {
        if (viewModel.FindSong(songId) is null) return;
        SearchResults.CapturePosition();
        ContentFrame.Navigate(typeof(ShellPage), new ShellNavigationRequest("歌曲详情:" + songId, this, songId), new EntranceNavigationTransitionInfo());
    }
    internal async Task QueueSearchVersionAsync(VersionHit version)
    {
        // Resolve the concrete version again. Never substitute a sibling version or cover.
        var ids = version.Resources.OrderByDescending(resource => resource.Resolution).ThenBy(resource => resource.SourceOrder)
            .ThenBy(resource => resource.Id, StringComparer.Ordinal).Select(resource => resource.SongId).Append(version.SongId).Distinct();
        foreach (string id in ids)
        {
            var item = viewModel.FindSong(id);
            if (item is null || !item.Song.IsAvailable) continue;
            await QueueSongAsync(item);
            return;
        }
        viewModel.ReportProblem("该演唱版本的文件不可用，请刷新曲库后重试。");
    }

    private void CloseSearchPresentation()
    {
        searchRequests.Dispose(); viewModel.SearchIndexChanged -= OnSearchIndexChanged;
        if (searchTextBox is null) return;
        searchTextBox.TextCompositionStarted -= OnSearchCompositionStarted;
        searchTextBox.TextCompositionEnded -= OnSearchCompositionEnded;
    }

    internal static T? FindSearchPart<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindSearchPart<T>(child) is { } nested) return nested;
        }
        return null;
    }
}
