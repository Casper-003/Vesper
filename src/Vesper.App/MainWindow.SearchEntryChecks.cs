using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Windows.Foundation;

namespace Vesper.App;

public sealed partial class MainWindow
{
    private async Task<object> CheckSearchEntryAsync(List<string> failures)
    {
        if (Environment.GetEnvironmentVariable("VESPER_RUNTIME_CHECK_PATH") is not { Length: > 0 } || canSave)
            throw new InvalidOperationException("Search-entry checks require isolated runtime mode with preference writes disabled.");
        bool originalOpen = Navigation.IsPaneOpen;
        string originalText = GlobalSearchBox.Text;
        var originalSelection = Navigation.SelectedItem;
        var originalPage = ContentFrame.Content;
        var back = ContentFrame.BackStack.ToArray();
        var forward = ContentFrame.ForwardStack.ToArray();
        var originalFocus = FocusManager.GetFocusedElement(Root.XamlRoot) as Control;
        var spacing = new List<object>();
        var movement = new List<object>();
        var limitations = new List<string>();
        int checks = 0, navigations = 0, clicks = 0;
        bool invoked = false, focused = false;
        void Check(bool condition, string message)
        { checks++; if (!condition) failures.Add("Search entry: " + message); }
        void OnNavigated(object sender, NavigationEventArgs args) => navigations++;
        void OnClicked(object sender, RoutedEventArgs args) => clicks++;
        static double Distance(Point a, Point b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
        Point Center(FrameworkElement element) => element.TransformToVisual(Root)
            .TransformPoint(new Point(element.ActualWidth / 2, element.ActualHeight / 2));
        Point Position() => new(SearchGlyphPosition.X, SearchGlyphPosition.Y);
        bool Drawn(FrameworkElement element)
        {
            if (!element.IsLoaded || element.ActualWidth <= 0 || element.ActualHeight <= 0) return false;
            for (DependencyObject? node = element; node is not null; node = VisualTreeHelper.GetParent(node))
                if (node is UIElement ui && (ui.Visibility != Visibility.Visible || ui.Opacity < .01)) return false;
            return true;
        }
        int GlyphCount() => new[] { compactSearchGlyph, SearchQueryGlyph, SearchMovingGlyph }.Count(Drawn);
        void CheckSettled(bool open)
        {
            Check(Navigation.IsPaneOpen == open && !searchGlyphPending && searchGlyphAnimation is null
                && SearchMovingGlyph.Visibility == Visibility.Collapsed && GlyphCount() == 1,
                $"{(open ? "expanded" : "compact")} endpoint must have exactly one native glyph and no moving overlay.");
        }
        void CheckSpacing()
        {
            var toggle = FindSearchPart<Button>(Navigation, "TogglePaneButton")
                ?? throw new InvalidOperationException("Native pane toggle was not created.");
            var area = FindSearchPart<Grid>(Navigation, "AutoSuggestArea")
                ?? throw new InvalidOperationException("Native search area was not created.");
            var favorite = Navigation.MenuItems.OfType<NavigationViewItem>().First(item => (string)item.Tag == "收藏");
            double[] centers = [Center(toggle).Y, Center(area).Y, Center(HomeItem).Y, Center(favorite).Y];
            double[] gaps = [centers[1] - centers[0], centers[2] - centers[1], centers[3] - centers[2]];
            spacing.Add(new { Open = Navigation.IsPaneOpen, Centers = centers, Gaps = gaps });
            Check(gaps.All(gap => Math.Abs(gap - 40) <= 1.5),
                $"toggle/search/home/favorite centers must keep the forty-unit rhythm ({string.Join(",", gaps.Select(gap => gap.ToString("F2")))}).");
            Check(Math.Abs(Center(Navigation.IsPaneOpen ? SearchQueryGlyph : compactSearchGlyph).Y - centers[1]) <= 1.5,
                "the visible search glyph must remain centered in its native search row.");
        }
        async Task<Storyboard?> RunningMotion()
        {
            for (int attempt = 0; attempt < 8; attempt++)
            {
                Root.UpdateLayout();
                if (!searchGlyphPending && searchGlyphAnimation is { } animation) return animation;
                await Task.Delay(16);
            }
            return null;
        }
        void CheckMiddle(Point from, Point to, string label)
        {
            Point middle = Position();
            movement.Add(new { Label = label, From = from, Middle = middle, To = to });
            Check(Distance(from, to) > 2 && Distance(middle, from) > .5 && Distance(middle, to) > .5
                && middle.X >= Math.Min(from.X, to.X) - 1 && middle.X <= Math.Max(from.X, to.X) + 1,
                $"{label} must interpolate between distinct native glyph positions.");
            Check(SearchMovingGlyph.Visibility == Visibility.Visible && compactSearchGlyph.Opacity == 0
                && SearchQueryGlyph.Opacity == 0 && GlyphCount() == 1,
                "in-flight movement must paint one moving glyph and hide both native copies.");
        }
        ContentFrame.Navigated += OnNavigated;
        try
        {
            await Task.Delay(150); Root.UpdateLayout();
            if (compactSearchButton is null) throw new InvalidOperationException("Native compact search button was not initialized.");
            compactSearchButton.Click += OnClicked;
            GlobalSearchBox.Text = "搜索回归 mixed 123";
            Navigation.IsPaneOpen = false;
            await Task.Delay(450); Root.UpdateLayout();
            CheckSettled(false); CheckSpacing();

            if (systemUi.AnimationsEnabled)
            {
                Point start = new(Center(compactSearchGlyph).X - 10, Center(compactSearchGlyph).Y - 10);
                Navigation.IsPaneOpen = true;
                var opening = await RunningMotion();
                Check(opening is not null, "opening must start the search glyph animation.");
                if (opening is not null)
                {
                    opening.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(100));
                    CheckMiddle(start, searchGlyphTarget, "opening");
                    Point beforeReverse = Position();
                    Navigation.IsPaneOpen = false;
                    Check(Distance(Position(), beforeReverse) <= 1, "rapid reversal must retain the current animated position before retargeting.");
                    var closing = await RunningMotion();
                    Check(closing is not null, "rapid reversal must start the return animation.");
                    Check(Distance(searchGlyphTarget, start) <= 1,
                        "closing must target the final compact rail, not the temporarily expanded pane width.");
                    if (closing is not null)
                    {
                        closing.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(100));
                        CheckMiddle(beforeReverse, searchGlyphTarget, "rapid reversal");
                    }
                }
                Navigation.IsPaneOpen = false;
                await Task.Delay(450); Root.UpdateLayout();
                CheckSettled(false);
            }
            else limitations.Add("System animations are disabled; midpoint and in-flight reversal checks were skipped.");

            var provider = new ButtonAutomationPeer(compactSearchButton).GetPattern(PatternInterface.Invoke) as IInvokeProvider;
            if (provider is null)
            {
                limitations.Add("Native search button has no Invoke provider; physical click and focus remain manual checks.");
                Navigation.IsPaneOpen = true;
            }
            else { provider.Invoke(); invoked = true; }
            await Task.Delay(450); Root.UpdateLayout();
            CheckSettled(true); CheckSpacing();
            for (DependencyObject? node = FocusManager.GetFocusedElement(Root.XamlRoot) as DependencyObject;
                node is not null; node = VisualTreeHelper.GetParent(node))
                if (ReferenceEquals(node, GlobalSearchBox)) { focused = true; break; }
            if (invoked) Check(clicks == 1 && Navigation.IsPaneOpen && focused,
                "the native compact search Click must expand the pane and focus its input.");
            Check(GlobalSearchBox.Text == "搜索回归 mixed 123" && ReferenceEquals(Navigation.SelectedItem, originalSelection)
                && ReferenceEquals(ContentFrame.Content, originalPage) && navigations == 0
                && ContentFrame.BackStack.SequenceEqual(back) && ContentFrame.ForwardStack.SequenceEqual(forward),
                "typing, pane motion and native search Click must retain input, navigation selection, page and browser history.");
        }
        finally
        {
            if (compactSearchButton is not null) compactSearchButton.Click -= OnClicked;
            GlobalSearchBox.Text = originalText;
            Navigation.IsPaneOpen = originalOpen;
            await Task.Delay(450);
            originalFocus?.Focus(FocusState.Programmatic);
            ContentFrame.Navigated -= OnNavigated;
        }
        return new { Checks = checks, Spacing = spacing, Movement = movement, NativeClickInvoked = invoked,
            NativeClickFocusedInput = focused, Limitations = limitations,
            Note = "Isolated native controls and Invoke pattern, not desktop input or visual acceptance. Preference writes stay disabled throughout the runtime suite." };
    }
}
