using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Vesper.App;

public sealed record ShellNavigationRequest(string Key, MainWindow Host, string? SongId = null,
    string? EntityId = null, bool IsAlbum = false);

public sealed partial class ShellPage : Page
{
    public ShellPage() => InitializeComponent();
    public string PageKey { get; private set; } = "";

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not ShellNavigationRequest request) return;
        PageKey = request.Key;
        request.Host.AttachPageContent(this, request.Key, request.SongId, request.EntityId, request.IsAlbum);
    }
}
