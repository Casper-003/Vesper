using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Vesper.Core;
using Windows.Foundation;

namespace Vesper.App;

public sealed partial class MainWindow
{
    private async Task<object> CheckNavigationInteractionsAsync(List<string> failures)
    {
        if (Environment.GetEnvironmentVariable("VESPER_NAVIGATION_CHECK_PATH") is not { Length: > 0 }
            || viewModel.Library.Songs.Count != 0)
            throw new InvalidOperationException("Navigation interactions require the fresh isolated navigation runner.");
        int checks = 0, captures = 0, observedDirections = 0;
        var motionSamples = new List<object>();
        var routeSamples = new List<object>();
        void Check(bool passed, string message)
        { checks++; if (!passed) failures.Add("Navigation interactions: " + message); }
        async Task Settle(int turns = 24)
        { for (int i = 0; i < turns; i++) { await Task.Delay(16); Root.UpdateLayout(); } }
        static Hyperlink[] Links(SongDetailsView view, string name) =>
            ((TextBlock)view.FindName(name)).Inlines.OfType<Hyperlink>().ToArray();
        static void Invoke(SongDetailsView view, string name, int index = 0)
        {
            var peer = new TextBlockAutomationPeer((TextBlock)view.FindName(name));
            var link = peer.GetChildren()?.Where(child => child.GetAutomationControlType() == AutomationControlType.Hyperlink).ElementAt(index)
                ?? throw new InvalidOperationException("Native text field has no hyperlink accessibility child.");
            var provider = link.GetPattern(PatternInterface.Invoke) as IInvokeProvider
                ?? throw new InvalidOperationException("Native entity hyperlink has no Invoke provider.");
            provider.Invoke();
        }
        string Current() => (ContentFrame.Content as ShellPage)?.PageKey ?? "";
        void SealLookup(string id)
        {
            var input = viewModel.GetLookupInput(id)!;
            viewModel.Library.Details[id] = input.Details with
            { Lookup = new(input.Key, DateTimeOffset.UtcNow, LookupOutcome.Matched, LookupOutcome.Matched) };
        }

        // No paths, remote URLs or media. Completed lookup records make loading
        // the real details control a cache hit instead of starting any provider.
        const string songId = "navigation-link-song";
        var state = new LibraryState();
        foreach (var (id, metadata) in new (string, SongMetadata)[]
        {
            (songId, new("Link Song", "Link Artist A / Link Artist B", "Shared Release", Year: "2026",
                Lyrics: string.Join('\n', Enumerable.Range(0, 48).Select(index => $"Synthetic lyric line {index:D2}")))
                { ArtistCredits = [new("Link Artist A"), new("Link Artist B"), new("Composer Only", "composer")] }),
            ("navigation-other-release", new("Other Song", "Other Artist", "Shared Release", Year: "2026")),
            ("navigation-empty", new("Empty Song")),
            ("navigation-unknown", new("Unknown Song", "未知艺人", "未知专辑"))
        })
        {
            state.Songs.Add(new(id, metadata.Title, []));
            var details = new SongDetails(Local: metadata);
            var input = new SongLookupInput(metadata.Title, details);
            state.Details[id] = details with
            { Lookup = new(input.Key, DateTimeOffset.UtcNow, LookupOutcome.Matched, LookupOutcome.Matched) };
        }
        await viewModel.Store.SaveAsync(state);
        await viewModel.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(15));
        viewModel.HideUnavailable = false;
        await viewModel.EnsureSearchIndexAsync().WaitAsync(TimeSpan.FromSeconds(15));
        foreach (var row in viewModel.Results) row.MetadataLoaded = true;
        Check(!viewModel.ReadOnly && viewModel.Library.Songs.Count == 4, "Synthetic details library did not initialize.");
        Check(state.Songs.All(song => viewModel.GetLookupInput(song.Id) is { } input && !input.ShouldLookup(DateTimeOffset.UtcNow)),
            "The synthetic details fixture would trigger a provider.");

        OpenEntitySong(songId); await Settle();
        string detailsKey = "歌曲详情:" + songId;
        var detailsView = (SongDetailsView)pageContent[detailsKey];
        var targets = GetSongEntityLinks(songId);
        Check(Current() == detailsKey && detailsView.IsLoaded, "Real song details did not mount.");
        Check(Links(detailsView, "ArtistValue").Length == 2 && targets.Artists.Count == 2
            && targets.Artists.All(artist => artist.Name != "Composer Only"), "Only actual performer credits should be linked.");
        Check(Links(detailsView, "AlbumValue").Length == 1 && targets.Album is not null
            && targets.Album.Id != GetSongEntityLinks("navigation-other-release").Album?.Id,
            "Album link must use the song's exact membership, not a same-name album.");
        var artistControl = (TextBlock)detailsView.FindName("ArtistValue");
        var albumControl = (TextBlock)detailsView.FindName("AlbumValue");
        Check(artistControl.FontSize == 16 && albumControl.FontSize == 16
            && artistControl.TextWrapping == TextWrapping.Wrap && albumControl.TextWrapping == TextWrapping.Wrap,
            "Interactive fields changed the existing size or wrapping.");
        Check(Links(detailsView, "ArtistValue").All(link => link.IsTabStop), "Performer links must support native keyboard focus.");
        var detailScroll = (ScrollViewer)detailsView.FindName("DetailsScroll");
        detailScroll.ChangeView(null, Math.Min(40, detailScroll.ScrollableHeight), null, true);
        await Settle(3);
        double detailsOffset = detailScroll.VerticalOffset;
        Check(detailsOffset > 1, "The details fixture did not exercise a nonzero scroll position.");
        for (int index = 0; index < targets.Artists.Count; index++)
        {
            Invoke(detailsView, "ArtistValue", index); await Settle();
            Check(Current() == "艺人:" + targets.Artists[index].Id, "Native performer link opened the wrong artist.");
            Check(GoShellBack(), "Artist link did not create a back route."); await Settle();
            Check(Current() == detailsKey && ReferenceEquals(detailsView, pageContent[detailsKey])
                && Math.Abs(detailScroll.VerticalOffset - detailsOffset) <= 1, "Returning from an artist lost the cached details view or scroll.");
        }
        Invoke(detailsView, "AlbumValue"); await Settle();
        Check(Current() == "专辑:" + targets.Album!.Id, "Native album link opened the wrong release.");
        Check(GoShellBack(), "Album link did not create a back route."); await Settle();
        Check(GoShellForward(), "Album link did not retain its forward route."); await Settle();
        Check(Current() == "专辑:" + targets.Album.Id, "Forward did not restore the exact album.");
        GoShellBack(); await Settle();
        string staleArtist = targets.Artists[0].Id, staleAlbum = targets.Album.Id;
        await viewModel.SaveCorrectionsAsync(songId, "Link Song", "Corrected Artist", "Corrected Release");
        SealLookup(songId); await Settle();
        int historyBeforeStaleClick = ContentFrame.BackStack.Count;
        OpenSongArtist(songId, staleArtist); OpenSongAlbum(songId, staleAlbum);
        Check(Current() == detailsKey && ContentFrame.BackStack.Count == historyBeforeStaleClick,
            "A stale entity target navigated after a song attribution correction.");
        var corrected = GetSongEntityLinks(songId);
        Check(corrected.Artists.Single().Name == "Corrected Artist" && corrected.Album?.Name == "Corrected Release"
            && Links(detailsView, "ArtistValue").Length == 1 && Links(detailsView, "AlbumValue").Length == 1,
            "Loaded links did not refresh after a correction.");
        Invoke(detailsView, "ArtistValue"); await Settle();
        Check(Current() == "艺人:" + corrected.Artists.Single().Id, "Corrected native artist link retained its old target.");
        GoShellBack(); await Settle();
        await viewModel.SaveCorrectionsAsync(songId, "Link Song", "", ""); SealLookup(songId); await Settle();
        Check(Links(detailsView, "ArtistValue").Length == 0 && Links(detailsView, "AlbumValue").Length == 0,
            "Explicitly cleared attribution remained clickable.");
        var unknown = new SongDetailsView(this, "navigation-unknown");
        Check(Links(unknown, "ArtistValue").Length == 0 && Links(unknown, "AlbumValue").Length == 0,
            "Unknown labels produced artificial entity links.");
        Check(automaticLookup is null && videoLookups.Count == 0, "The isolated checks started an online provider.");

        // Marker bodies isolate native transition selection. Detail requests use
        // actual fixture identities; production has no special handling of these keys.
        var markers = new Dictionary<string, Border>();
        foreach (var (key, channel, color) in new[]
        {
            ("__slide-a", 2, Windows.UI.Color.FromArgb(255, 230, 0, 0)),
            ("__slide-b", 1, Windows.UI.Color.FromArgb(255, 0, 210, 0)),
            ("__slide-c", 0, Windows.UI.Color.FromArgb(255, 0, 0, 230)),
            ("__slide-d", 0, Windows.UI.Color.FromArgb(255, 0, 0, 230))
        })
        {
            var marker = new Border { Width = 32, Height = 120, HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(280, 100, 0, 0),
                Background = new SolidColorBrush(color), Tag = channel };
            markers.Add(key, marker);
            var grid = new Grid(); grid.Children.Add(marker); pageContent.Add(key, grid);
        }
        ShellNavigationRequest RequestFor(string key) => key switch
        {
            "__slide-c" => new(key, this, SongId: songId),
            "__slide-d" => new(key, this, EntityId: GetSongEntityLinks("navigation-other-release").Artists.Single().Id),
            _ => new(key, this)
        };
        async Task<double?> MarkerPosition(Border marker, bool horizontal)
        {
            captures++;
            var capture = await CaptureQueueRootAsync();
            var point = marker.TransformToVisual(Root).TransformPoint(new Point(16, 60));
            int fixedX = (int)Math.Round(point.X * capture.Width / Root.ActualWidth);
            int fixedY = (int)Math.Round(point.Y * capture.Height / Root.ActualHeight);
            if (fixedX < 0 || fixedX >= capture.Width || fixedY < 0 || fixedY >= capture.Height) return null;
            for (int position = 0; position < (horizontal ? capture.Width : capture.Height); position++)
            {
                int x = horizontal ? position : fixedX, y = horizontal ? fixedY : position;
                int offset = (y * capture.Width + x) * 4;
                int channel = (int)marker.Tag;
                int lead = capture.Pixels[offset + channel];
                int other = Enumerable.Range(0, 3).Where(value => value != channel).Max(value => (int)capture.Pixels[offset + value]);
                if (lead > 70 && lead - other > 35) return horizontal
                    ? x * Root.ActualWidth / capture.Width : y * Root.ActualHeight / capture.Height;
            }
            return null;
        }
        async Task Probe(string target, string label, NavigationMode mode, SlideNavigationTransitionEffect? expectedEffect,
            Func<bool> navigate, bool captureMotion = false)
        {
            NavigationEventArgs? navigation = null;
            SlideNavigationTransitionEffect? recordedEffect = null;
            void CaptureNavigation(object sender, NavigationEventArgs args)
            {
                navigation = args;
                // The shared object's Effect changes on later history traversals.
                // Copy the value during this event, never retain it as edge state.
                recordedEffect = (args.NavigationTransitionInfo as SlideNavigationTransitionInfo)?.Effect;
            }
            int backCount = ContentFrame.BackStack.Count, forwardCount = ContentFrame.ForwardStack.Count;
            ContentFrame.Navigated += CaptureNavigation;
            try { Check(navigate(), label + ": navigation was rejected."); }
            finally { ContentFrame.Navigated -= CaptureNavigation; }
            Check(navigation is { } actual && actual.NavigationMode == mode
                && actual.Parameter is ShellNavigationRequest request && request.Key == target,
                label + ": native navigation mode/target was changed.");
            Check(mode switch
            {
                NavigationMode.New => ContentFrame.BackStack.Count == backCount + 1 && ContentFrame.ForwardStack.Count == 0,
                NavigationMode.Back => ContentFrame.BackStack.Count == backCount - 1 && ContentFrame.ForwardStack.Count == forwardCount + 1,
                NavigationMode.Forward => ContentFrame.BackStack.Count == backCount + 1 && ContentFrame.ForwardStack.Count == forwardCount - 1,
                _ => false
            }, label + ": native history was duplicated or rewritten.");
            if (expectedEffect is { } expected)
            {
                Check(shellSlideTransition.Effect == expected, label + ": selected the wrong axis for this history edge.");
                if (mode == NavigationMode.Back)
                    Check(ReferenceEquals(ContentFrame.ForwardStack.Last().NavigationTransitionInfo, shellSlideTransition),
                        label + ": Back did not install the shared native Forward transition.");
                else
                    Check(ReferenceEquals(navigation?.NavigationTransitionInfo, shellSlideTransition) && recordedEffect == expected,
                        label + ": the native route did not receive the shared transition with the expected axis.");
            }
            else Check(navigation?.NavigationTransitionInfo is SuppressNavigationTransitionInfo,
                label + ": Create's sidebar-only suppression was lost.");
            routeSamples.Add(new { Label = label, Target = target, Mode = mode.ToString(),
                ExpectedEffect = expectedEffect?.ToString() ?? "Suppress", RecordedEffect = recordedEffect?.ToString(),
                RecordedTransition = navigation?.NavigationTransitionInfo?.GetType().Name });
            if (!captureMotion)
            {
                await Settle();
                Check(Current() == target, label + ": wrong target page.");
                return;
            }
            bool horizontal = expectedEffect == SlideNavigationTransitionEffect.FromRight;
            int direction = mode == NavigationMode.Back ? -1 : 1;
            var positions = new List<double>();
            var transforms = new List<double>();
            for (int i = 0; i < 5; i++)
            {
                var transform = markers[target].TransformToVisual(Root).TransformPoint(default);
                transforms.Add(horizontal ? transform.X : transform.Y);
                if (await MarkerPosition(markers[target], horizontal) is { } position) positions.Add(position);
                await Task.Delay(16);
            }
            await Settle();
            double final = await MarkerPosition(markers[target], horizontal) ?? throw new InvalidOperationException("Settled slide marker is missing.");
            Check(Current() == target, label + ": wrong target page.");
            bool intermediateFrame = positions.Any(x => Math.Abs(x - final) > 2);
            if (intermediateFrame)
            {
                observedDirections++;
                Check(positions.Any(x => direction * (x - final) > 2)
                    && positions.All(x => direction * (x - final) >= -2), label + ": observed incoming motion used the wrong side.");
            }
            motionSamples.Add(new { Label = label, Axis = horizontal ? "Horizontal" : "Vertical", Direction = direction, IntermediateFrame = intermediateFrame,
                RecordedTransition = navigation?.NavigationTransitionInfo?.GetType().Name,
                Positions = positions, Transforms = transforms, Final = final });
        }
        try
        {
            const SlideNavigationTransitionEffect vertical = SlideNavigationTransitionEffect.FromBottom;
            const SlideNavigationTransitionEffect horizontal = SlideNavigationTransitionEffect.FromRight;
            NavigateShell(RequestFor("__slide-a")); await Settle();
            await Probe("__slide-b", "main-new", NavigationMode.New, vertical, () => NavigateShell(RequestFor("__slide-b")), true);
            await Probe("__slide-c", "detail-new", NavigationMode.New, horizontal, () => NavigateShell(RequestFor("__slide-c")), true);
            await Probe("__slide-a", "detail-to-sidebar", NavigationMode.New, vertical, () => NavigateShell(RequestFor("__slide-a"), fromSidebar: true));
            await Probe("__slide-c", "back-sidebar-to-detail", NavigationMode.Back, vertical, GoShellBack, true);
            await Probe("__slide-b", "back-detail-to-main", NavigationMode.Back, horizontal, GoShellBack);
            await Probe("__slide-c", "forward-detail", NavigationMode.Forward, horizontal, GoShellForward, true);
            await Probe("__slide-a", "forward-main", NavigationMode.Forward, vertical, GoShellForward, true);

            // Revisit the same song key twice with a main page between visits.
            NavigateShell(RequestFor("__slide-c")); await Settle();
            NavigateShell(RequestFor("__slide-b"), fromSidebar: true); await Settle();
            NavigateShell(RequestFor("__slide-c")); await Settle();
            NavigateShell(RequestFor("__slide-d")); await Settle();
            NavigateShell(RequestFor("设置"), fromSidebar: true); await Settle();
            foreach (var (target, effect) in new[] { ("__slide-d", vertical), ("__slide-c", horizontal),
                ("__slide-b", horizontal), ("__slide-c", vertical), ("__slide-a", horizontal) })
                await Probe(target, "mixed-back-" + routeSamples.Count, NavigationMode.Back, effect, GoShellBack);
            foreach (var (target, effect) in new[] { ("__slide-c", horizontal), ("__slide-b", vertical),
                ("__slide-c", horizontal), ("__slide-d", horizontal), ("设置", vertical) })
                await Probe(target, "mixed-forward-" + routeSamples.Count, NavigationMode.Forward, effect, GoShellForward);

            NavigateShell(RequestFor("__slide-c")); await Settle();
            await Probe("搜索", "detail-to-search", NavigationMode.New, vertical, () => NavigateShell(RequestFor("搜索")));
            await Probe("__slide-c", "back-search-to-detail", NavigationMode.Back, vertical, GoShellBack);
            int historyCount = ContentFrame.BackStack.Count;
            Check(!NavigateShell(RequestFor("__slide-c")) && ContentFrame.BackStack.Count == historyCount,
                "Reselecting a cached detail created another history entry.");
            await Probe("创作", "sidebar-create", NavigationMode.New, null, () => NavigateShell(RequestFor("创作"), fromSidebar: true));
            Check(!ContentFrame.CanGoForward, "A new sidebar route retained stale Forward history.");
            await Probe("__slide-d", "create-to-detail", NavigationMode.New, horizontal, () => NavigateShell(RequestFor("__slide-d")));
            await Probe("创作", "back-detail-to-create", NavigationMode.Back, horizontal, GoShellBack);
            await Probe("__slide-c", "back-create-to-detail", NavigationMode.Back, vertical, GoShellBack);
            await Probe("创作", "forward-create", NavigationMode.Forward, vertical, GoShellForward);
            await Probe("__slide-d", "forward-detail-across-create", NavigationMode.Forward, horizontal, GoShellForward);
        }
        finally
        {
            NavigateShell(new("首页", this)); await Settle();
            foreach (string key in markers.Keys)
            {
                if (pageOwners.Remove(key, out var owner)) owner.Content = null;
                pageContent.Remove(key);
            }
        }
        return new { CheckCount = checks, RootCaptures = captures, ObservedDirectionSamples = observedDirections,
            DirectionCaptureStatus = observedDirections == 5 ? "Observed" : "Inconclusive: some or all captures contain only the settled endpoint.",
            AnimationsEnabled = systemUi.AnimationsEnabled, RouteSamples = routeSamples, MotionSamples = motionSamples,
            Limitations = "Synthetic isolated library and native Hyperlink Invoke / Frame history. Native transition axes and mixed history integrity are checked; endpoint-only captures do not prove motion direction. Physical keyboard/pointer behavior, actual horizontal/vertical animation and real-library visual acceptance remain manual." };
    }
}
