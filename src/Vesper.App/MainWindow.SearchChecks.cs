using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Vesper.Core;

namespace Vesper.App;

public sealed partial class MainWindow
{
    // Selected before the VM is constructed. An opt-in test must never load the
    // user's library, including when initialization fails before its first query.
    private static string? SearchCheckStorePath()
    {
        string? report = Environment.GetEnvironmentVariable("VESPER_SEARCH_CHECK_PATH")
            ?? Environment.GetEnvironmentVariable("VESPER_NAVIGATION_CHECK_PATH");
        if (report is null) return null;
        if (string.IsNullOrWhiteSpace(report))
            throw new InvalidOperationException("VESPER_SEARCH_CHECK_PATH requires a report filename.");
        string directory = Path.GetDirectoryName(Path.GetFullPath(report))!;
        return Path.Combine(directory, $"search-runtime-isolated-{Environment.ProcessId}", "library.json");
    }

    // Exercises the real UI scheduling, paging, Frame restoration and queue bridge.
    // Synthetic sources are existence probes only; they are never decoded or played.
    private async Task RunSearchRuntimeChecksAsync(string reportPath)
    {
        canSave = false;
        var failures = new List<string>();
        var checks = new List<object>();
        var diagnostics = new List<object>();
        var limitations = new List<string>
        {
            "Native IME candidate input and physical keyboard/pointer behavior remain Windows manual checks; core checks cover publication races with deliberately delayed work.",
            "Search → Home → Back exercises the shared Frame restoration path. A real song-details page is not opened, avoiding automatic online metadata/lyrics lookup.",
            "Queue checks preserve the existing playback model's paused/playing state; no synthetic media is decoded and no real audio playback is tested.",
            "The version picker uses explicitly trusted synthetic group/version IDs and resource qualities to exercise the real controls. Current legacy library records have no proven version relations or quality metadata; the regression does not infer them.",
            "IME-ended callback invalidation is checked by calling the existing handlers with synthetic event arguments; this does not simulate a Windows IME candidate session."
        };
        var timer = Stopwatch.StartNew();
        string? storePath = SearchCheckStorePath();
        int navigations = 0;
        void Check(bool passed, string description)
        {
            checks.Add(new { Description = description, Passed = passed });
            if (!passed) failures.Add(description);
        }
        void OnNavigated(object sender, Microsoft.UI.Xaml.Navigation.NavigationEventArgs args) => navigations++;
        async Task WaitFor(Func<bool> condition, string description, int attempts = 150)
        {
            for (int i = 0; i < attempts; i++)
            {
                Root.UpdateLayout();
                if (condition()) return;
                // Bounded native dispatcher / layout settling, not a randomized
                // engine race test. Core race checks use controlled completions.
                await Task.Delay(20);
            }
            throw new TimeoutException(description);
        }
        async Task Settle(int turns = 12)
        { for (int i = 0; i < turns; i++) { await Task.Delay(20); Root.UpdateLayout(); } }
        T Part<T>(string name) where T : class => SearchResults.FindName(name) as T
            ?? throw new InvalidOperationException("Missing search view part: " + name);
        IEnumerable<Button> Buttons(DependencyObject root)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is Button button) yield return button;
                foreach (var nested in Buttons(child)) yield return nested;
            }
        }
        void Invoke(Button button)
        {
            var provider = new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke) as IInvokeProvider
                ?? throw new InvalidOperationException("Native button has no Invoke provider.");
            provider.Invoke();
        }
        string PageKey() => (ContentFrame.Content as ShellPage)?.PageKey ?? "";
        void SetQuery(string text)
        {
            restoringSearchText = true;
            try { GlobalSearchBox.Text = text; }
            finally { restoringSearchText = false; }
            ScheduleSearch(text, true);
        }
        async Task WaitForQuery(string text) => await WaitFor(() => PageKey() == "搜索"
            && SearchResults.Query == text && SearchResults.Revision == viewModel.IndexRevision,
            "Query did not publish: " + text);

        ContentFrame.Navigated += OnNavigated;
        try
        {
            if (storePath is null) throw new InvalidOperationException("Search checks require an isolated store.");
            string mediaDirectory = Path.Combine(Path.GetDirectoryName(storePath)!, "media");
            Directory.CreateDirectory(mediaDirectory);
            var state = new LibraryState { Directories = [mediaDirectory] };
            for (int i = 0; i < 44; i++)
            {
                string path = Path.Combine(mediaDirectory, $"runtime-{i:D2}.mp4");
                await File.WriteAllTextAsync(path, "Synthetic search probe " + new string('x', i));
                string id = $"search-runtime-{i:D2}";
                string prefix = i == 42 ? "Needle" : i == 43 ? "Second" : "Runtime";
                state.Songs.Add(new(id, $"fallback-{i:D2}", [path]));
                state.Details[id] = new(Local: new($"{prefix} Track {i:D2}", $"{prefix} Artist", $"{prefix} Album"));
            }
            await new LibraryStore(storePath).SaveAsync(state);
            await viewModel.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(15));
            await viewModel.EnsureSearchIndexAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Check(viewModel.IsSearchReady && !viewModel.ReadOnly && viewModel.Results.Count == 44,
                "Isolated synthetic library and immutable index initialize without personal media.");

            string initialPage = PageKey();
            int initialHistory = ContentFrame.BackStack.Count;
            GlobalSearchBox.Focus(FocusState.Programmatic);
            await Settle(2);
            Check(PageKey() == initialPage && ContentFrame.BackStack.Count == initialHistory,
                "Focusing the empty sidebar search retains the current page and history.");

            SetQuery("Needle");
            await WaitForQuery("Needle");
            Check(ContentFrame.BackStack.Count == initialHistory + 1 && SearchResults.LoadedSongCount == 1,
                "The first nonempty debounced query navigates once and binds its song result.");
            Check(Part<FrameworkElement>("ArtistCard").Visibility == Visibility.Visible
                && Part<FrameworkElement>("AlbumCard").Visibility == Visibility.Visible
                && Part<TextBlock>("ArtistName").Text == "Needle Artist"
                && Part<TextBlock>("AlbumName").Text == "Needle Album",
                "The same query binds independent matching artist, album and song categories.");

            int searchHistory = ContentFrame.BackStack.Count;
            SetQuery("Runtime");
            SearchRequest? beforeClear = pendingSearch;
            SetQuery("");
            await Settle();
            Check(PageKey() == "搜索" && SearchResults.Query == "" && SearchResults.LoadedSongCount == 0
                && beforeClear is not null && beforeClear.CancellationToken.IsCancellationRequested
                && !searchRequests.CanPublish(beforeClear),
                "Clearing immediately empties the current search page and rejects the earlier scheduled query.");
            Check(Part<FrameworkElement>("ArtistCard").Visibility == Visibility.Collapsed
                && Part<FrameworkElement>("AlbumCard").Visibility == Visibility.Collapsed,
                "Clearing removes category cards as well as song rows.");

            SetQuery("Runtime");
            SearchRequest? superseded = pendingSearch;
            SetQuery("Second");
            await WaitForQuery("Second");
            await Settle();
            Check(SearchResults.Query == "Second" && SearchResults.LoadedSongCount == 1
                && Part<ListView>("Songs").Items.Cast<SearchResultRow>().Single().Title == "Second Track 43"
                && superseded is not null && !searchRequests.CanPublish(superseded),
                "A newer query owns publication; the superseded UI request cannot refill previous results.");
            Check(ContentFrame.BackStack.Count == searchHistory,
                "Changing keywords and clearing do not add navigation history entries.");

            SetQuery("definitely-no-match-123xyz");
            await WaitForQuery("definitely-no-match-123xyz");
            Check(SearchResults.LoadedSongCount == 0 && Part<TextBlock>("SearchState").Text == "无匹配",
                "A completed nonempty query with no category hits displays 无匹配.");

            SetQuery("Runtime");
            await WaitForQuery("Runtime");
            var songs = Part<ListView>("Songs");
            Check(SearchResults.LoadedSongCount == 30 && Part<Button>("MoreSongs").Visibility == Visibility.Visible,
                "The real first result binding contains thirty song groups and offers another page.");
            var invoke = new ButtonAutomationPeer(Part<Button>("MoreSongs")).GetPattern(PatternInterface.Invoke) as IInvokeProvider;
            if (invoke is null) throw new InvalidOperationException("Native MoreSongs button has no Invoke provider.");
            invoke.Invoke();
            await WaitFor(() => SearchResults.LoadedSongCount == 42, "The real more-results Click did not append its page.");
            Check(songs.Items.Cast<SearchResultRow>().Select(row => row.Hit.GroupId).Distinct().Count() == 42
                && Part<Button>("MoreSongs").Visibility == Visibility.Collapsed,
                "Appending the second page preserves distinct group identities and reaches the end.");

            songs.ScrollIntoView(songs.Items[33], ScrollIntoViewAlignment.Leading);
            await Settle();
            var panel = songs.ItemsPanelRoot as ItemsStackPanel
                ?? throw new InvalidOperationException("Search songs did not realize an ItemsStackPanel.");
            int firstIndex = Math.Clamp(panel.FirstVisibleIndex, 0, songs.Items.Count - 1);
            string anchor = ((SearchResultRow)songs.Items[firstIndex]).Hit.GroupId;
            double offset = songs.ContainerFromIndex(firstIndex) is FrameworkElement firstContainer
                ? firstContainer.TransformToVisual(songs).TransformPoint(default).Y : double.NaN;
            Check(firstIndex > 0 && double.IsFinite(offset), "A scrolled result anchor is realized before leaving search.");
            SearchResults.CapturePosition();
            ContentFrame.Navigate(typeof(ShellPage), new ShellNavigationRequest("首页", this));
            await WaitFor(() => PageKey() == "首页", "Home navigation failed during restoration check.");
            ContentFrame.GoBack();
            await WaitFor(() => PageKey() == "搜索" && SearchResults.IsLoaded, "Back did not reattach the search view.");
            await Settle();
            panel = songs.ItemsPanelRoot as ItemsStackPanel
                ?? throw new InvalidOperationException("Restored search lost its ItemsStackPanel.");
            int restoredIndex = Math.Clamp(panel.FirstVisibleIndex, 0, songs.Items.Count - 1);
            string restoredAnchor = ((SearchResultRow)songs.Items[restoredIndex]).Hit.GroupId;
            double restoredOffset = songs.ContainerFromIndex(restoredIndex) is FrameworkElement restoredContainer
                ? restoredContainer.TransformToVisual(songs).TransformPoint(default).Y : double.NaN;
            Check(GlobalSearchBox.Text == "Runtime" && SearchResults.Query == "Runtime"
                && SearchResults.LoadedSongCount == 42 && restoredAnchor == anchor
                && double.IsFinite(restoredOffset) && Math.Abs(restoredOffset - offset) <= 3,
                "Frame Back restores the keyword, loaded pages, group anchor and relative row offset.");

            var firstSong = viewModel.FindSong("search-runtime-00")
                ?? throw new InvalidOperationException("Synthetic playback seed disappeared.");
            viewModel.AddToQueue(firstSong);
            _ = viewModel.Queue.StartNext(viewModel.Library.Songs, out _);
            viewModel.Queue.MarkPaused();
            QueueEntry? current = viewModel.Queue.Current;
            var pausedVersion = viewModel.SearchIndex!.Search("Runtime Track 01", 9001).Songs.Items.Single().Versions.Single();
            int pending = viewModel.Queue.Pending.Count;
            await QueueSearchVersionAsync(pausedVersion);
            Check(viewModel.Queue.Pending.Count == pending + 1 && viewModel.Queue.Pending.Last().SongId == "search-runtime-01"
                && viewModel.Queue.Current == current && viewModel.Queue.State == PlaybackState.Paused,
                "The actual search-to-existing-queue bridge queues the concrete ID while paused playback remains unchanged.");
            viewModel.Queue.MarkPlaying();
            var playingVersion = viewModel.SearchIndex.Search("Runtime Track 02", 9002).Songs.Items.Single().Versions.Single();
            pending = viewModel.Queue.Pending.Count;
            await QueueSearchVersionAsync(playingVersion);
            Check(viewModel.Queue.Pending.Count == pending + 1 && viewModel.Queue.Pending.Last().SongId == "search-runtime-02"
                && viewModel.Queue.Current == current && viewModel.Queue.State == PlaybackState.Playing
                && PageKey() == "搜索" && GlobalSearchBox.Text == "Runtime" && SearchResults.LoadedSongCount == 42,
                "Continuous search queueing keeps playing state, current song, keyword and loaded search results.");

            string originalPath = viewModel.FindSong("search-runtime-03")!.Song.Sources.Single();
            string livePath = viewModel.FindSong("search-runtime-04")!.Song.Sources.Single();
            var versionIndex = LocalSearchIndex.Build(
            [
                new SearchDocument("search-runtime-03", "Version Probe", "Runtime Artist", "Runtime Album", true)
                {
                    GroupId = "runtime-version-group", VersionId = "runtime-original", VersionName = "原版 MV",
                    IsOriginalMv = true, AddedAt = DateTimeOffset.Parse("2020-01-01T00:00:00Z"),
                    Resources = [new("runtime-original-720", "search-runtime-03", originalPath, true, 720)]
                },
                new SearchDocument("search-runtime-04", "Version Probe", "Runtime Artist", "Runtime Album", true)
                {
                    GroupId = "runtime-version-group", VersionId = "runtime-live", VersionName = "现场版",
                    AddedAt = DateTimeOffset.Parse("2021-01-01T00:00:00Z"),
                    Resources = [new("runtime-live-2160", "search-runtime-04", livePath, true, 2160)]
                }
            ], viewModel.IndexRevision, true);
            var groupedResult = versionIndex.Search("Version Probe 现场版", 9003);
            SearchResults.Apply(groupedResult, false);
            await WaitFor(() => SearchResults.LoadedSongCount == 1 && songs.ContainerFromIndex(0) is ListViewItem,
                "The explicit version group did not realize a result row.");
            var groupRow = songs.Items.Cast<SearchResultRow>().Single();
            Check(groupRow.IsGroup && groupRow.Hit.RepresentativeVersionId == "runtime-original"
                && groupRow.Hit.BestMatchedVersionId == "runtime-live" && groupRow.MatchLabel == "命中：现场版"
                && groupRow.Hit.Versions.Single(version => version.VersionId == "runtime-original").Resolution == 720
                && groupRow.Hit.Versions.Single(version => version.VersionId == "runtime-live").Resolution == 2160,
                "A real grouped row keeps the original 720p representative while identifying the matched 2160p live version.");
            var groupContainer = songs.ContainerFromIndex(0) as ListViewItem
                ?? throw new InvalidOperationException("The grouped search row was not realized.");
            var pickerButton = Buttons(groupContainer).Single(button => button.Visibility == Visibility.Visible
                && Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(button) == "选择演唱版本");
            void RecordPicker(string stage, int clickCount)
            {
                var context = pickerButton.DataContext;
                var parent = VisualTreeHelper.GetParent(pickerButton) as FrameworkElement;
                diagnostics.Add(new
                {
                    Stage = stage,
                    DataContextType = context?.GetType().FullName,
                    Title = (context as SearchResultRow)?.Title,
                    GroupId = (context as SearchResultRow)?.Hit.GroupId,
                    SameRow = ReferenceEquals(context, groupRow),
                    pickerButton.IsEnabled,
                    pickerButton.IsLoaded,
                    Visibility = pickerButton.Visibility.ToString(),
                    pickerButton.ActualWidth,
                    pickerButton.ActualHeight,
                    ParentDataContextType = parent?.DataContext?.GetType().FullName,
                    ContainerContentType = groupContainer.Content?.GetType().FullName,
                    ContainerContentTitle = (groupContainer.Content as SearchResultRow)?.Title,
                    ContainerDataContextType = groupContainer.DataContext?.GetType().FullName,
                    TemplateDataContextType = (groupContainer.ContentTemplateRoot as FrameworkElement)?.DataContext?.GetType().FullName,
                    ClickCount = clickCount,
                    VersionChoicesType = SearchResults.VersionChoices?.GetType().FullName
                });
            }
            RecordPicker("group-button-realized", 0);
            await Settle(6);
            groupContainer = songs.ContainerFromIndex(0) as ListViewItem
                ?? throw new InvalidOperationException("The grouped search row disappeared during layout settling.");
            pickerButton = Buttons(groupContainer).Single(button => button.Visibility == Visibility.Visible
                && Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(button) == "选择演唱版本");
            RecordPicker("before-native-invoke", 0);
            Check(pickerButton.IsLoaded && pickerButton.IsEnabled && pickerButton.DataContext is SearchResultRow { IsGroup: true }
                && ReferenceEquals(pickerButton.DataContext, groupRow),
                "The realized native version button is loaded, enabled and bound to the current grouped result row.");
            int pickerClicks = 0;
            RoutedEventHandler pickerClickProbe = (_, _) => pickerClicks++;
            pickerButton.Click += pickerClickProbe;
            try
            {
                Invoke(pickerButton);
                await WaitFor(() => SearchResults.VersionChoices is ScrollViewer { Content: StackPanel },
                    "The real version-button Click did not open its flyout.");
            }
            finally
            {
                RecordPicker("after-native-invoke", pickerClicks);
                pickerButton.Click -= pickerClickProbe;
            }
            var choiceScroll = (ScrollViewer)SearchResults.VersionChoices!;
            var choices = (StackPanel)choiceScroll.Content;
            Check(choices.Children.OfType<Grid>().Count() == 2,
                "The actual version flyout displays both independently identified versions.");
            var liveLine = choices.Children.OfType<Grid>().Single(line => line.Children.OfType<Button>()
                .Any(button => button.Tag is VersionHit version && version.VersionId == "runtime-live"));
            var liveAdd = liveLine.Children.OfType<Button>().Single(button => button.Tag is VersionHit version
                && version.VersionId == "runtime-live");
            var liveOpen = liveLine.Children.OfType<Button>().Single(button => !ReferenceEquals(button, liveAdd));
            await WaitFor(() => ReferenceEquals(FocusManager.GetFocusedElement(Root.XamlRoot), liveOpen),
                "The opened version picker did not focus the best matched live version.");
            Check(liveAdd.IsEnabled && liveAdd.Tag is VersionHit { SongId: "search-runtime-04" },
                "The matched live choice is enabled and its native add button carries the concrete version identity.");
            pending = viewModel.Queue.Pending.Count;
            Invoke(liveAdd);
            await WaitFor(() => viewModel.Queue.Pending.Count == pending + 1,
                "The native live-version add Click did not reach the existing queue.");
            Check(viewModel.Queue.Pending.Last().SongId == "search-runtime-04" && viewModel.Queue.Current == current
                && viewModel.Queue.State == PlaybackState.Playing,
                "Invoking the flyout's live-version add button queues that version, preserving the original current song and playing state.");

            // The delegate schedules a deferred refresh onto the dispatcher. Leave
            // synchronously before that queued callback runs, exercising its guard.
            SearchResults.Clear();
            await Settle();
            deferredSearchRefresh = true;
            SearchResults.InteractionEnded?.Invoke();
            SearchRequest? beforeInteractionLeave = pendingSearch;
            ContentFrame.Navigate(typeof(ShellPage), new ShellNavigationRequest("首页", this));
            await Settle();
            Check(PageKey() == "首页" && beforeInteractionLeave is not null
                && !searchRequests.CanPublish(beforeInteractionLeave),
                "A deferred InteractionEnded refresh queued before navigation cannot pull the user back into search.");

            SetQuery("Second");
            await WaitForQuery("Second");
            Navigation.IsPaneOpen = true;
            await Settle();
            AttachSearchInput();
            if (searchTextBox is null) throw new InvalidOperationException("The native sidebar search TextBox was not attached.");
            OnSearchCompositionStarted(searchTextBox, null!);
            OnSearchCompositionEnded(searchTextBox, null!);
            SearchRequest? beforeImeLeave = pendingSearch;
            ContentFrame.Navigate(typeof(ShellPage), new ShellNavigationRequest("首页", this));
            await Settle();
            Check(PageKey() == "首页" && !composingSearch && beforeImeLeave is not null
                && !searchRequests.CanPublish(beforeImeLeave),
                "An IME-ended handler callback queued before navigation cannot publish or reopen search after leaving.");
            Check(await viewModel.FlushQueueAsync(), "The queued synthetic search selections persist to the isolated store.");
        }
        catch (Exception ex) { failures.Add(ex.ToString()); }
        finally
        {
            ContentFrame.Navigated -= OnNavigated;
            try
            {
                string path = Path.GetFullPath(reportPath);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
                {
                    Passed = failures.Count == 0,
                    Checks = checks,
                    CheckCount = checks.Count,
                    Diagnostics = diagnostics,
                    Failures = failures,
                    Limitations = limitations,
                    StorePath = storePath,
                    Navigations = navigations,
                    ElapsedMilliseconds = timer.Elapsed.TotalMilliseconds
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            finally { Close(); }
        }
    }
}
