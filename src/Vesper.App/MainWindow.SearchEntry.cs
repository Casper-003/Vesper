using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;

namespace Vesper.App;

public sealed partial class MainWindow
{
    private Button? compactSearchButton;
    private readonly FontIcon compactSearchGlyph = new() { Glyph = "\uE721", FontSize = 16 };
    private Storyboard? searchGlyphAnimation;
    private Point? searchGlyphRestPosition;
    private Point searchGlyphTarget;
    private bool searchGlyphPending;

    private void OnSearchNavigationLoaded(object sender, RoutedEventArgs args)
    {
        // WinUI reserves an extra eight units below this slot. Use the same
        // forty-unit rhythm as the toggle and navigation rows, in both states.
        if (FindSearchPart<Grid>(Navigation, "AutoSuggestArea") is { } area)
            area.Margin = new Thickness(0);
        compactSearchButton = FindSearchPart<Button>(Navigation, "PaneAutoSuggestButton");
        if (compactSearchButton is null) return;
        compactSearchButton.Content = compactSearchGlyph;
        Navigation.LayoutUpdated -= OnSearchEntryLayoutUpdated;
        Navigation.LayoutUpdated += OnSearchEntryLayoutUpdated;
    }

    private static T? FindSearchPart<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T part && part.Name == name) return part;
            if (FindSearchPart<T>(child, name) is { } nested) return nested;
        }
        return null;
    }

    private void BeginSearchGlyphTransition()
    {
        if (closed || compactSearchButton is null || searchGlyphRestPosition is not { } from) return;
        if (searchGlyphAnimation is not null || searchGlyphPending)
            from = new Point(SearchGlyphPosition.X, SearchGlyphPosition.Y);
        searchGlyphAnimation?.Stop();
        searchGlyphAnimation = null;
        SearchGlyphPosition.X = from.X;
        SearchGlyphPosition.Y = from.Y;
        compactSearchGlyph.Opacity = SearchQueryGlyph.Opacity = 0;
        SearchMovingGlyph.Visibility = Visibility.Visible;
        searchGlyphPending = true;
    }

    private void OnSearchEntryLayoutUpdated(object? sender, object args)
    {
        if (closed || compactSearchButton is null) return;
        FrameworkElement target = Navigation.IsPaneOpen ? SearchQueryGlyph : compactSearchGlyph;
        if (!target.IsLoaded || target.ActualWidth <= 0 || target.ActualHeight <= 0) return;
        var center = target.TransformToVisual(Root).TransformPoint(new Point(target.ActualWidth / 2, target.ActualHeight / 2));
        // WinUI keeps the pane's expanded width until its close transition ends.
        // Its compact button can temporarily be centered in that wider grid;
        // aim directly at the compact rail, not that intermediate position.
        if (!Navigation.IsPaneOpen)
            center.X = Navigation.TransformToVisual(Root).TransformPoint(new Point(Navigation.CompactPaneLength / 2, 0)).X;
        var destination = new Point(center.X - 10, center.Y - 10);
        searchGlyphRestPosition = destination;
        bool targetMoved = Math.Abs(searchGlyphTarget.X - destination.X) > .5 || Math.Abs(searchGlyphTarget.Y - destination.Y) > .5;
        if (searchGlyphPending || (searchGlyphAnimation is not null && targetMoved))
        {
            searchGlyphPending = false;
            if (!systemUi.AnimationsEnabled) { SettleSearchGlyph(); return; }
            // Native compact layout can settle over more than one pass. Retarget
            // from the displayed position rather than snapping to the new endpoint.
            double x = SearchGlyphPosition.X, y = SearchGlyphPosition.Y;
            searchGlyphAnimation?.Stop();
            SearchGlyphPosition.X = x;
            SearchGlyphPosition.Y = y;
            var storyboard = new Storyboard();
            AddSearchGlyphAnimation(storyboard, nameof(TranslateTransform.X), SearchGlyphPosition.X, destination.X);
            AddSearchGlyphAnimation(storyboard, nameof(TranslateTransform.Y), SearchGlyphPosition.Y, destination.Y);
            storyboard.Completed += (_, _) =>
            {
                if (!ReferenceEquals(searchGlyphAnimation, storyboard) || closed) return;
                SettleSearchGlyph();
            };
            searchGlyphAnimation = storyboard;
            storyboard.Begin();
        }
        searchGlyphTarget = destination;
    }

    private void AddSearchGlyphAnimation(Storyboard storyboard, string property, double from, double to)
    {
        var move = new DoubleAnimation
        {
            From = from, To = to, Duration = TimeSpan.FromMilliseconds(200),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(move, SearchGlyphPosition);
        Storyboard.SetTargetProperty(move, property);
        storyboard.Children.Add(move);
    }

    private void SettleSearchGlyph()
    {
        searchGlyphAnimation?.Stop();
        searchGlyphAnimation = null;
        searchGlyphPending = false;
        SearchMovingGlyph.Visibility = Visibility.Collapsed;
        compactSearchGlyph.Opacity = SearchQueryGlyph.Opacity = 1;
    }
}
