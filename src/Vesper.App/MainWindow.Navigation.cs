using Microsoft.UI.Xaml.Media.Animation;

namespace Vesper.App;

public sealed partial class MainWindow
{
    private static SlideNavigationTransitionInfo ShellSlideTransition() => new()
    {
        // WinUI reverses this effect for a Back navigation: new/forward pages
        // enter from the right, while the Back destination enters from the left.
        Effect = SlideNavigationTransitionEffect.FromRight
    };

    private bool NavigateShell(ShellNavigationRequest request, bool fromSidebar = false)
    {
        string? current = (ContentFrame.Content as ShellPage)?.PageKey;
        if (closed || current == request.Key) return false;

        // Keep Create's established sidebar-only exception. Its independent
        // sidebar width animation is untouched by this page-navigation policy.
        bool suppress = fromSidebar && (current == "创作" || request.Key == "创作");
        NavigationTransitionInfo transition = suppress ? new SuppressNavigationTransitionInfo() : ShellSlideTransition();
        return ContentFrame.Navigate(typeof(ShellPage), request, transition);
    }

    private bool GoShellBack()
    {
        if (closed || !ContentFrame.CanGoBack) return false;
        // Always override Back, including Create history, so a remembered
        // Entrance/Suppress transition cannot choose this traversal's effect.
        ContentFrame.GoBack(ShellSlideTransition());
        return true;
    }

    private bool GoShellForward()
    {
        if (closed || !ContentFrame.CanGoForward) return false;
        // WinUI exposes only parameterless GoForward and reuses its last override.
        // A forward route exists only after GoShellBack, which always sets the
        // same FromRight effect. New navigation clears that route, including a
        // sidebar Create navigation. Leave native history entries unchanged.
        ContentFrame.GoForward();
        return true;
    }
}
