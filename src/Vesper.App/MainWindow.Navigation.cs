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
        { BeginPagePreparation(request, () => NavigatePreparedShell(request)); return true; }
        CancelPagePreparation();
        return NavigatePreparedShell(request);
    }

    private bool NavigatePreparedShell(ShellNavigationRequest request)
    {
        var transition = SelectShellTransition(request);
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
        SelectShellTransition(ContentFrame.ForwardStack.Last().Parameter as ShellNavigationRequest);
        ContentFrame.GoForward();
        return true;
    }
}
