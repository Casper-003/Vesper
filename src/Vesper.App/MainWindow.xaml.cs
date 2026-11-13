using System.Text.Json;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.Graphics;
using Windows.UI.ViewManagement;

namespace Vesper.App;

public sealed partial class MainWindow : Window
{
    private readonly string preferencesPath = Path.Combine(Environment.GetFolderPath(
        Environment.SpecialFolder.LocalApplicationData), "Vesper", "shell-preferences.json");
    private readonly UISettings systemUi = new();
    private readonly Dictionary<string, FrameworkElement> pageContent = [];
    private readonly Dictionary<string, ShellPage> pageOwners = [];
    private bool ready;
    private bool changingSelection;
    private bool closed;
    private bool canSave = true;
    private MonitoringPanel monitoring = null!;
    private readonly UiResponsivenessTrace? responsivenessTrace;
    private bool themeApplied, appliedDark;
    private Windows.UI.Color appliedAccent;
    private int themeSignals, themeRepaints;

    public MainWindow()
    {
        ShellPreferences preferences = new();
        try
        {
            if (Environment.GetEnvironmentVariable("VESPER_NAVIGATION_CHECK_PATH") is { Length: > 0 }
                || Environment.GetEnvironmentVariable("VESPER_ARTWORK_CHECK_PATH") is { Length: > 0 }
                || Environment.GetEnvironmentVariable("VESPER_PAGE_CHECK_PATH") is { Length: > 0 }
                || Environment.GetEnvironmentVariable("VESPER_COLOR_CHECK_PATH") is { Length: > 0 }) canSave = false;
            else if (File.Exists(preferencesPath))
                preferences = JsonSerializer.Deserialize<ShellPreferences>(File.ReadAllText(preferencesPath)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { canSave = false; }
        InitializeComponent();
        if (Environment.GetEnvironmentVariable("VESPER_PERF_TRACE_PATH") is { Length: > 0 } tracePath)
            responsivenessTrace = new(DispatcherQueue, tracePath, GetOverviewDiagnostics);
        PageArea.DataContext = viewModel;
        InitializeLibraryPresentation();
        InitializeQueuePresentation();
        InitializeSearchPresentation();
        viewModel.HideUnavailable = preferences.HideUnavailable;
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        SetTitleBar(AppTitleBar);
        InitializePreview();
        InitializeSinging(preferences);
        monitoring = new MonitoringPanel(() => viewModel.Queue.Current is not null || preview.SongId is not null
            || singingEntryRequested || previewEntryRequested);
        AppearanceContent.Children.Add(monitoring);
        Closed += (_, _) => monitoring.Dispose();
        InitializeHome();
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        AppWindow.Resize(new SizeInt32(1200, 800));
        ApplyWindowMinimumSize(1);
        Navigation.IsPaneOpen = preferences.PaneOpen;
        Navigation.Loaded += OnSearchNavigationLoaded;
        activityDisplay.IsPaneOpen = Navigation.IsPaneOpen;
        Navigation.SelectedItem = HomeItem;
        if (Navigation.SettingsItem is NavigationViewItem settingsItem) settingsItem.Content = "设置";
        VesperAccent.IsChecked = !preferences.SystemAccent;
        SystemAccent.IsChecked = preferences.SystemAccent;
        ready = true;
        Navigation.RegisterPropertyChangedCallback(NavigationView.IsPaneOpenProperty, (_, _) =>
        {
            if (!ready) return;
            activityDisplay.IsPaneOpen = Navigation.IsPaneOpen;
            BeginSearchGlyphTransition();
            SavePreferences();
        });
        systemUi.ColorValuesChanged += OnSystemColorsChanged;
        Closed += (_, _) => { closed = true; responsivenessTrace?.Dispose(); systemUi.ColorValuesChanged -= OnSystemColorsChanged; CloseSearchPresentation(); CloseQueuePresentation(); CloseOverviewScrollbars(); CloseLibraryPresentation(); };
        Activated += (_, _) => ApplyTheme();
        NavigateShell(new ShellNavigationRequest("首页", this));
        PageLoading.Visibility = Visibility.Visible;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Navigation.SettingsItem is NavigationViewItem settingsItem) settingsItem.Content = "设置";
        ApplyTheme(); UpdateTitleLayout();
        if (IsIsolatedUiCheck()) PageLoading.Visibility = Visibility.Collapsed;
        if (!canSave) PreferenceNotice.IsOpen = true;
        if (Environment.GetEnvironmentVariable("VESPER_COLOR_CHECK_PATH") is { Length: > 0 } colorCheckPath)
        {
            if (!libraryInitialized) { libraryInitialized = true; await RunColorChecksAsync(colorCheckPath); }
            return;
        }
        if (Environment.GetEnvironmentVariable("VESPER_PAGE_CHECK_PATH") is { Length: > 0 } pageCheckPath)
        {
            PageLoading.Visibility = Visibility.Collapsed;
            if (!libraryInitialized) { libraryInitialized = true; await RunPagePreparationChecksAsync(pageCheckPath); }
            return;
        }
        if (Environment.GetEnvironmentVariable("VESPER_ARTWORK_CHECK_PATH") is { Length: > 0 } artworkCheckPath)
        {
            if (!libraryInitialized) { libraryInitialized = true; await RunArtworkChecksAsync(artworkCheckPath); }
            return;
        }
        if (Environment.GetEnvironmentVariable("VESPER_NAVIGATION_CHECK_PATH") is { Length: > 0 } navigationCheckPath)
        {
            if (!libraryInitialized) { libraryInitialized = true; await RunOverviewNavigationChecksAsync(navigationCheckPath); }
            return;
        }
        if (Environment.GetEnvironmentVariable("VESPER_SEARCH_CHECK_PATH") is { Length: > 0 } searchCheckPath)
        {
            if (!libraryInitialized) { libraryInitialized = true; await RunSearchRuntimeChecksAsync(searchCheckPath); }
            return;
        }
        if (Environment.GetEnvironmentVariable("VESPER_QUEUE_CHECK_PATH") is { Length: > 0 } queueCheckPath)
        {
            if (!libraryInitialized) { libraryInitialized = true; await RunQueueRuntimeChecksAsync(queueCheckPath); }
            return;
        }
        if (Environment.GetEnvironmentVariable("VESPER_RUNTIME_CHECK_PATH") is { Length: > 0 })
        {
            if (!libraryInitialized) { libraryInitialized = true; await RunOverviewRuntimeChecksAsync(); }
            return;
        }
        if (!libraryInitialized)
        {
            libraryInitialized = true;
            long startupRevision = pagePreparationRevision;
            SetPagePreparationProgress(0, 0, "正在准备曲库…");
            PageLoading.Visibility = Visibility.Visible;
            try
            {
                await viewModel.InitializeAsync();
                if (!closed && (ContentFrame.Content as ShellPage)?.PageKey == "首页") await PrepareHomeResourcesAsync(metadataCancellation.Token);
            }
            finally
            {
                libraryPrepared.TrySetResult();
                if (startupRevision == pagePreparationRevision) PageLoading.Visibility = Visibility.Collapsed;
            }
            if (!closed) StartLibraryEnrichment();
            if (!closed && Environment.GetEnvironmentVariable("VESPER_PREVIEW_PAGE") is "曲库" or "收藏" &&
                Navigation.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(item =>
                    (string)item.Tag == Environment.GetEnvironmentVariable("VESPER_PREVIEW_PAGE")) is { } previewItem)
                Navigation.SelectedItem = previewItem;
            if (!closed && Environment.GetEnvironmentVariable("VESPER_PREVIEW_QUEUE") == "1") SetQueueOpen(true);
        }
    }

    private void OnSystemColorsChanged(UISettings sender, object args) => DispatcherQueue.TryEnqueue(() =>
    { if (!closed) ApplyTheme(); });

    private void ApplyTheme()
    {
        if (!ready) return;
        themeSignals++;
        object? light = Microsoft.Win32.Registry.GetValue(
            @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1);
        bool dark = light is int value && value == 0;
        Windows.UI.Color accent = SystemAccent.IsChecked == true
            ? systemUi.GetColorValue(UIColorType.Accent) : Windows.UI.Color.FromArgb(255, 0x39, 0xC5, 0xBB);
        if (themeApplied && dark == appliedDark && accent.Equals(appliedAccent)) return;
        themeRepaints++;
        if (!themeApplied || dark != appliedDark)
        {
            Root.RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;
            AppWindow.TitleBar.ButtonForegroundColor = dark ? Colors.White : Colors.Black;
            AppWindow.TitleBar.ButtonInactiveForegroundColor = dark ? Colors.Gray : Colors.DimGray;
            ApplyOverviewTheme(dark);
        }
        if (Navigation.Resources["NavigationViewSelectionIndicatorForeground"] is SolidColorBrush accentBrush)
            accentBrush.Color = accent;
        else Navigation.Resources["NavigationViewSelectionIndicatorForeground"] = new SolidColorBrush(accent);
        themeApplied = true; appliedDark = dark; appliedAccent = accent;
    }

    private void OnAccentChanged(object sender, RoutedEventArgs e)
    { if (ready) { ApplyTheme(); SavePreferences(); } }

    private void SavePreferences()
    {
        if (!canSave) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(preferencesPath)!);
            string temporary = preferencesPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new ShellPreferences(Navigation.IsPaneOpen,
                SystemAccent.IsChecked == true, viewModel.HideUnavailable, SeekDuringSingingSwitch.IsOn)));
            File.Move(temporary, preferencesPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { PreferenceNotice.IsOpen = true; }
    }

    private void OnSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (!ready || changingSelection) return;
        string? page = args.IsSettingsSelected ? "设置" : (args.SelectedItem as NavigationViewItem)?.Tag as string;
        if (page is null) return;
        NavigateShell(new ShellNavigationRequest(page, this), fromSidebar: true);
    }

    private void OnBack(object sender, RoutedEventArgs e)
    { GoShellBack(); }

    private void OnForward(object sender, RoutedEventArgs e)
    { GoShellForward(); }

    private void OnFrameNavigated(object sender, NavigationEventArgs e)
    {
        if (e.Parameter is not ShellNavigationRequest request) return;
        string page = request.Key;
        bool showQueue = request.SongId is not null || page != "创作";
        SetQueueSidebarVisible(showQueue);
        if (showQueue) SetQueueOpen(false);
        changingSelection = true;
        try
        {
            Navigation.SelectedItem = page == "设置" ? Navigation.SettingsItem
                : Navigation.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(item => (string)item.Tag == page);
        }
        finally { changingSelection = false; }
        PageTitle.Text = request.SongId is not null ? "歌曲详情" : page;
        bool overview = request.EntityId is not null || request.SongId is null && page is ("首页" or "曲库" or "收藏" or "创作");
        PageHeading.Visibility = overview ? Visibility.Collapsed : Visibility.Visible;
        PageArea.Padding = overview ? new Thickness(0) : new Thickness(28, 24, 28, 24);
        LibraryActions.Visibility = page == "曲库" ? Visibility.Visible : Visibility.Collapsed;
        SongFeedbackPanel.Visibility = page is "曲库" or "收藏" or "搜索" || request.SongId is not null || request.EntityId is not null ? Visibility.Visible : Visibility.Collapsed;
        BackButton.IsEnabled = ContentFrame.CanGoBack;
        ForwardButton.IsEnabled = ContentFrame.CanGoForward;
        OnSearchPageNavigated(page);
        DispatcherQueue.TryEnqueue(UpdateTitleLayout);
    }

    internal void AttachPageContent(ShellPage page, string key, string? songId, string? entityId = null, bool isAlbum = false)
    {
        if (!pageContent.TryGetValue(key, out FrameworkElement? content))
        {
            content = songId is not null ? new SongDetailsView(this, songId)
                : entityId is not null ? CreateEntityDetails(key, entityId, isAlbum)
                : key switch { "首页" => HomePage, "曲库" => LibraryPage, "收藏" => FavoritesPage, "创作" => CreationPage, "设置" => AppearancePanel, "搜索" => SearchResults, _ => new Grid() };
            PageStorage.Children.Remove(content);
            pageContent[key] = content;
        }
        if (pageOwners.TryGetValue(key, out ShellPage? previous)) previous.Content = null;
        if (content is EntityDetailsView entity && entityId is not null) RefreshEntityDetails(entity, entityId, isAlbum);
        PrepareOverviewBackground(key, songId);
        content.Visibility = Visibility.Visible;
        if (songId is null) content.DataContext = viewModel;
        page.Content = content;
        pageOwners[key] = page;
    }

    private void OnLayoutChanged(object sender, SizeChangedEventArgs e)
    {
        if (!ready) return;
        if (DirectoryFlyout.IsOpen) DirectoryFlyout.Hide();
        UpdateTitleLayout();
    }

    private void UpdateTitleLayout()
    {
        if (Root.XamlRoot is null || Root.ActualWidth <= 0) return;
        Root.UpdateLayout();
        double scale = Root.XamlRoot.RasterizationScale;
        ApplyWindowMinimumSize(scale);
        double left = HistoryArea.TransformToVisual(Root).TransformPoint(default).X + HistoryArea.ActualWidth;
        double right = Root.ActualWidth - AppWindow.TitleBar.RightInset / scale;
        var regions = new List<RectInt32>();
        void Add(double x, double end)
        {
            if (end > x) regions.Add(new RectInt32((int)Math.Ceiling(x * scale), 0,
                (int)Math.Floor((end - x) * scale), (int)Math.Round(AppTitleBar.ActualHeight * scale)));
        }
        if (capsule is not null && capsule.Bar.ActualWidth > 0)
        {
            double capsuleLeft = capsule.Bar.TransformToVisual(Root).TransformPoint(default).X;
            Add(left, Math.Max(left, capsuleLeft));
            Add(Math.Min(right, capsuleLeft + capsule.Bar.ActualWidth), right);
        }
        else Add(left, right);
        AppWindow.TitleBar.SetDragRectangles(regions.ToArray());
    }

    private void ApplyWindowMinimumSize(double scale)
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter) return;
        int width = (int)Math.Round(900 * scale);
        int height = (int)Math.Round(600 * scale);
        if (presenter.PreferredMinimumWidth != width) presenter.PreferredMinimumWidth = width;
        if (presenter.PreferredMinimumHeight != height) presenter.PreferredMinimumHeight = height;
        if (presenter.State == OverlappedPresenterState.Restored
            && (AppWindow.Size.Width < width || AppWindow.Size.Height < height))
            AppWindow.Resize(new SizeInt32(Math.Max(AppWindow.Size.Width, width), Math.Max(AppWindow.Size.Height, height)));
    }
}
