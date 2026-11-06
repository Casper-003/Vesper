using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Vesper.App;

public sealed partial class HomeContent : UserControl
{
    private sealed class Shelf
    {
        internal readonly ObservableCollection<HomeCard> Cards = [];
        internal readonly Grid Root = new() { RowSpacing = 12, Visibility = Visibility.Collapsed };
        internal readonly ListView List = new() { SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = true,
            IsSwipeEnabled = false, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(0), MinWidth = 0 };
        internal readonly Button Previous = Arrow("\uE76B", "向左浏览"), Next = Arrow("\uE76C", "向右浏览");
        internal ScrollViewer? Viewer;
        internal event Action? ViewportChanged;
        internal Shelf(string name, double size, DataTemplate template)
        {
            Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var heading = new Grid { Height = 34 };
            heading.Children.Add(new TextBlock { Text = name, FontSize = 22, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Right };
            actions.Children.Add(Previous); actions.Children.Add(Next); heading.Children.Add(actions); Root.Children.Add(heading);
            List.Height = size + 76; List.ItemsSource = Cards; List.ItemTemplate = template;
            List.ItemsPanel = (ItemsPanelTemplate)XamlReader.Load("<ItemsPanelTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><ItemsStackPanel Orientation='Horizontal'/></ItemsPanelTemplate>");
            List.ItemContainerStyle = (Style)XamlReader.Load("<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='ListViewItem'><Setter Property='Padding' Value='0'/><Setter Property='Margin' Value='0,0,16,0'/><Setter Property='CornerRadius' Value='8'/><Setter Property='HorizontalContentAlignment' Value='Stretch'/></Style>");
            ScrollViewer.SetHorizontalScrollMode(List, ScrollMode.Disabled); ScrollViewer.SetVerticalScrollMode(List, ScrollMode.Disabled);
            ScrollViewer.SetHorizontalScrollBarVisibility(List, ScrollBarVisibility.Hidden); ScrollViewer.SetVerticalScrollBarVisibility(List, ScrollBarVisibility.Disabled);
            Grid.SetRow(List, 1); Root.Children.Add(List);
            Previous.Click += (_, _) => Scroll(-1, size + 16); Next.Click += (_, _) => Scroll(1, size + 16);
            List.Loaded += (_, _) => EnsureViewer();
            List.SizeChanged += (_, _) => { EnsureViewer(); UpdateArrows(); };
        }
        private void EnsureViewer()
        {
            var viewer = FindViewer(List);
            if (viewer is null || ReferenceEquals(viewer, Viewer)) return;
            Viewer = viewer; viewer.ViewChanged += (_, _) => { UpdateArrows(); ViewportChanged?.Invoke(); }; viewer.SizeChanged += (_, _) => UpdateArrows();
            viewer.RegisterPropertyChangedCallback(ScrollViewer.ScrollableWidthProperty, (_, _) => UpdateArrows());
            UpdateArrows();
        }
        internal void UpdateArrows()
        {
            bool scrollable = Viewer?.ScrollableWidth > 1;
            Previous.Visibility = Next.Visibility = scrollable ? Visibility.Visible : Visibility.Collapsed;
            Previous.IsEnabled = scrollable && Viewer!.HorizontalOffset > 1;
            Next.IsEnabled = scrollable && Viewer!.HorizontalOffset < Viewer.ScrollableWidth - 1;
        }
        private void Scroll(int direction, double stride)
        {
            if (Viewer is null) return;
            double distance = Math.Max(stride, Math.Floor(Viewer.ViewportWidth / stride) * stride);
            Viewer.ChangeView(Math.Clamp(Viewer.HorizontalOffset + direction * distance, 0, Viewer.ScrollableWidth), null, null,
                !new Windows.UI.ViewManagement.UISettings().AnimationsEnabled);
        }
        private static Button Arrow(string glyph, string name)
        {
            var button = new Button { Width = 34, Height = 34, Padding = new Thickness(0), BorderThickness = new Thickness(0),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), Content = new FontIcon { Glyph = glyph, FontSize = 14 } };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, name); ToolTipService.SetToolTip(button, name); return button;
        }
    }
    private readonly Shelf artists, recommendations, favorites;
    public event Action<HomeCard>? CardClicked;
    public event Action? ViewportChanged;
    public event Action<int>? VerticalWheel;
    public HomeContent()
    {
        InitializeComponent();
        var template = (DataTemplate)Resources["CardTemplate"];
        artists = new("艺人", 224, template); recommendations = new("推荐", 160, template); favorites = new("收藏", 160, template);
        foreach (var shelf in All)
        {
            Shelves.Children.Add(shelf.Root);
            shelf.List.ItemClick += (_, e) => { if (e.ClickedItem is HomeCard card) CardClicked?.Invoke(card); };
            shelf.List.ContainerContentChanging += (_, _) => ViewportChanged?.Invoke();
            shelf.ViewportChanged += () => ViewportChanged?.Invoke();
            shelf.List.Loaded += (_, _) => ViewportChanged?.Invoke();
            shelf.List.SizeChanged += (_, _) => ViewportChanged?.Invoke();
            shelf.List.AddHandler(PointerWheelChangedEvent, new PointerEventHandler((_, e) =>
            { VerticalWheel?.Invoke(e.GetCurrentPoint(shelf.List).Properties.MouseWheelDelta); e.Handled = true; }), true);
        }
    }
    private Shelf[] All => [artists, recommendations, favorites];
    internal void Bind(IReadOnlyList<HomeCard> artistCards, IReadOnlyList<HomeCard> recommended, IReadOnlyList<HomeCard> favoriteCards)
    {
        Sync(artists, artistCards); Sync(recommendations, recommended); Sync(favorites, favoriteCards);
        Visibility = artistCards.Count + recommended.Count + favoriteCards.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
    private static void Sync(Shelf shelf, IReadOnlyList<HomeCard> desired)
    {
        var wanted = desired.ToHashSet();
        for (int i = shelf.Cards.Count - 1; i >= 0; i--) if (!wanted.Contains(shelf.Cards[i])) shelf.Cards.RemoveAt(i);
        var existing = shelf.Cards.ToHashSet();
        for (int i = 0; i < desired.Count; i++)
        {
            if (i < shelf.Cards.Count && ReferenceEquals(shelf.Cards[i], desired[i])) continue;
            if (existing.Add(desired[i])) shelf.Cards.Insert(i, desired[i]);
            else shelf.Cards.Move(shelf.Cards.IndexOf(desired[i]), i);
        }
        shelf.Root.Visibility = desired.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        shelf.UpdateArrows();
    }
    internal IEnumerable<HomeCard> VisibleCards(FrameworkElement viewport)
    {
        foreach (var shelf in All)
        {
            if (shelf.Root.Visibility != Visibility.Visible || shelf.List.ItemsPanelRoot is not ItemsStackPanel panel) continue;
            for (int i = Math.Max(0, panel.FirstVisibleIndex); i <= panel.LastVisibleIndex; i++)
            {
                if (shelf.List.ContainerFromIndex(i) is not ListViewItem row || row.Content is not HomeCard card) continue;
                Rect bounds = row.TransformToVisual(viewport).TransformBounds(new Rect(0, 0, row.ActualWidth, row.ActualHeight));
                if (bounds.Bottom > 48 && bounds.Top < viewport.ActualHeight && bounds.Right > 0 && bounds.Left < viewport.ActualWidth) yield return card;
            }
        }
    }
    internal IReadOnlyList<HomeCard> ArtistCards => artists.Cards;
    internal IReadOnlyList<HomeCard> RecommendedCards => recommendations.Cards;
    internal IReadOnlyList<HomeCard> FavoriteCards => favorites.Cards;
    internal IReadOnlyList<ListView> Lists => All.Select(shelf => shelf.List).ToArray();
    internal ScrollViewer? ArtistViewport => artists.Viewer;
    internal Button ArtistNext => artists.Next;
    internal bool FavoriteSectionVisible => favorites.Root.Visibility == Visibility.Visible;
    private static ScrollViewer? FindViewer(DependencyObject element)
    {
        if (element is ScrollViewer viewer) return viewer;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
            if (FindViewer(VisualTreeHelper.GetChild(element, i)) is { } found) return found;
        return null;
    }
}
