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

        // Probe native history and sample motion without treating snapshots
        // that contain only the animation endpoint as direction evidence.
        var markers = new Dictionary<string, Border>();
        foreach (var (key, channel, color) in new[]
        {
            ("__slide-a", 2, Windows.UI.Color.FromArgb(255, 230, 0, 0)),
            ("__slide-b", 1, Windows.UI.Color.FromArgb(255, 0, 210, 0)),
            ("__slide-c", 0, Windows.UI.Color.FromArgb(255, 0, 0, 230))
        })
        {
            var marker = new Border { Width = 32, Height = 120, HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(280, 100, 0, 0),
                Background = new SolidColorBrush(color), Tag = channel };
            markers.Add(key, marker);
            var grid = new Grid(); grid.Children.Add(marker); pageContent.Add(key, grid);
        }
        async Task<double?> MarkerLeft(Border marker)
        {
            captures++;
            var capture = await CaptureQueueRootAsync();
            var point = marker.TransformToVisual(Root).TransformPoint(new Point(0, 60));
            int y = (int)Math.Round(point.Y * capture.Height / Root.ActualHeight);
            if (y < 0 || y >= capture.Height) return null;
            for (int x = 0; x < capture.Width; x++)
            {
                int offset = (y * capture.Width + x) * 4;
                int channel = (int)marker.Tag;
                int lead = capture.Pixels[offset + channel];
                int other = Enumerable.Range(0, 3).Where(value => value != channel).Max(value => (int)capture.Pixels[offset + value]);
                if (lead > 70 && lead - other > 35) return x * Root.ActualWidth / capture.Width;
            }
            return null;
        }
        async Task Probe(string target, string label, NavigationMode mode, int direction, Func<bool> navigate)
        {
            NavigationEventArgs? navigation = null;
            void CaptureNavigation(object sender, NavigationEventArgs args) => navigation = args;
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
            Check(navigation?.NavigationTransitionInfo is SlideNavigationTransitionInfo { Effect: SlideNavigationTransitionEffect.FromRight },
                label + ": native route did not record the unified Slide effect.");
            if (mode == NavigationMode.Back)
                Check(ContentFrame.ForwardStack.Last().NavigationTransitionInfo is SlideNavigationTransitionInfo { Effect: SlideNavigationTransitionEffect.FromRight },
                    label + ": Back did not establish the Slide effect for the native forward route.");
            var positions = new List<double>();
            var transforms = new List<double>();
            for (int i = 0; i < 5; i++)
            {
                transforms.Add(markers[target].TransformToVisual(Root).TransformPoint(default).X);
                if (await MarkerLeft(markers[target]) is { } x) positions.Add(x);
                await Task.Delay(16);
            }
            await Settle();
            double final = await MarkerLeft(markers[target]) ?? throw new InvalidOperationException("Settled slide marker is missing.");
            Check(Current() == target, label + ": wrong target page.");
            bool intermediateFrame = positions.Any(x => Math.Abs(x - final) > 2);
            if (intermediateFrame)
            {
                observedDirections++;
                Check(positions.Any(x => direction * (x - final) > 2)
                    && positions.All(x => direction * (x - final) >= -2), label + ": observed incoming motion used the wrong side.");
            }
            motionSamples.Add(new { Label = label, Direction = direction, IntermediateFrame = intermediateFrame,
                RecordedTransition = navigation?.NavigationTransitionInfo?.GetType().Name,
                Positions = positions, Transforms = transforms, Final = final });
        }
        try
        {
            NavigateShell(new("__slide-a", this)); await Settle();
            await Probe("__slide-b", "new", NavigationMode.New, 1, () => NavigateShell(new("__slide-b", this)));
            await Probe("__slide-a", "back", NavigationMode.Back, -1, GoShellBack);
            await Probe("__slide-b", "forward", NavigationMode.Forward, 1, GoShellForward);
            NavigateShell(new("创作", this), fromSidebar: true); await Settle();
            NavigateShell(new("__slide-c", this), fromSidebar: true); await Settle();
            GoShellBack(); await Settle(); // Create
            await Probe("__slide-b", "back-across-create", NavigationMode.Back, -1, GoShellBack);
            GoShellForward(); await Settle(); // Create again
            await Probe("__slide-c", "consecutive-forward-across-create", NavigationMode.Forward, 1, GoShellForward);
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
            AnimationsEnabled = systemUi.AnimationsEnabled, MotionSamples = motionSamples,
            Limitations = "Synthetic isolated library and native Hyperlink Invoke / Frame history. Native transition selection and history integrity are checked; endpoint-only captures do not prove motion direction. Physical keyboard/pointer behavior, actual horizontal animation and real-library visual acceptance remain manual." };
    }
}
