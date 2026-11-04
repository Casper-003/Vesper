using Microsoft.UI.Xaml.Media.Animation;

namespace Vesper.App;

public sealed partial class MainWindow
{
    private readonly SlideNavigationTransitionInfo shellSlideTransition = ShellSlideTransition();

    private static SlideNavigationTransitionInfo ShellSlideTransition() => new()
    {
        Effect = SlideNavigationTransitionEffect.FromBottom
    };

    private SlideNavigationTransitionInfo SelectShellTransition(ShellNavigationRequest? incomingRequest)
    {
        // History entries share this mutable transition, so their current Effect
        // is not a record of an old edge. The immutable route describes its axis.
        shellSlideTransition.Effect = incomingRequest is { SongId: not null } or { EntityId: not null }
            ? SlideNavigationTransitionEffect.FromRight : SlideNavigationTransitionEffect.FromBottom;
        return shellSlideTransition;
    }

    private bool NavigateShell(ShellNavigationRequest request, bool fromSidebar = false)
    {
        string? current = (ContentFrame.Content as ShellPage)?.PageKey;
        if (closed) return false;
        if (current == request.Key) { CancelPagePreparation(); return false; }
        if (NeedsPagePreparation(request))
        { BeginPagePreparation(request, () => NavigatePreparedShell(request, fromSidebar)); return true; }
        CancelPagePreparation();
        return NavigatePreparedShell(request, fromSidebar);
    }

    private bool NavigatePreparedShell(ShellNavigationRequest request, bool fromSidebar)
    {
        string? current = (ContentFrame.Content as ShellPage)?.PageKey;

        // Keep Create's established sidebar-only exception. Its independent
        // sidebar width animation is untouched by this page-navigation policy.
        bool suppress = fromSidebar && (current == "创作" || request.Key == "创作");
        NavigationTransitionInfo transition = suppress ? new SuppressNavigationTransitionInfo() : SelectShellTransition(request);
        return ContentFrame.Navigate(typeof(ShellPage), request, transition);
    }

    private bool GoShellBack()
    {
        CancelPagePreparation();
        if (closed || !ContentFrame.CanGoBack) return false;
        if (ContentFrame.BackStack.Last().Parameter is ShellNavigationRequest target && NeedsPagePreparation(target))
        {
            BeginPagePreparation(target, () =>
            {
                if (!ContentFrame.CanGoBack || !ReferenceEquals(ContentFrame.BackStack.Last().Parameter, target)) return false;
                ContentFrame.GoBack(SelectShellTransition((ContentFrame.Content as ShellPage)?.Request));
                return true;
            });
            return true;
        }
        // Undo the edge that brought in the page being left. WinUI reverses the
        // selected effect for Back: details leave horizontally, root pages vertically.
        // Always provide the shared instance, including Create history.
        ContentFrame.GoBack(SelectShellTransition((ContentFrame.Content as ShellPage)?.Request));
        return true;
    }

    private bool GoShellForward()
    {
        CancelPagePreparation();
        if (closed || !ContentFrame.CanGoForward) return false;
        if (ContentFrame.ForwardStack.Last().Parameter is ShellNavigationRequest target && NeedsPagePreparation(target))
        {
            BeginPagePreparation(target, () =>
            {
                if (!ContentFrame.CanGoForward || !ReferenceEquals(ContentFrame.ForwardStack.Last().Parameter, target)) return false;
                SelectShellTransition(target); ContentFrame.GoForward(); return true;
            });
            return true;
        }
        // GoForward reuses the object installed by GoShellBack. Change that same
        // object's Effect before WinUI builds the next storyboard; this also works
        // across consecutive Forward traversals with different axes. A sidebar
        // Create navigation clears Forward, and the next Back installs this object again.
        SelectShellTransition(ContentFrame.ForwardStack.Last().Parameter as ShellNavigationRequest);
        ContentFrame.GoForward();
        return true;
    }
}
