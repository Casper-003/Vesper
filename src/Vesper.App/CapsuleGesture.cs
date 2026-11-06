namespace Vesper.App;

// Explicit press state: capture loss, a hold, or any drag permanently disqualifies a click.
internal sealed class CapsuleGesture
{
    private bool pressed, moved;
    private double x, y;
    private long at;
    public void Press(double startX, double startY, long milliseconds)
    { pressed = true; moved = false; x = startX; y = startY; at = milliseconds; }
    public bool Move(double currentX, double currentY)
    {
        if (!pressed || moved || Math.Abs(currentX - x) < 4 && Math.Abs(currentY - y) < 4) return false;
        moved = true; return true;
    }
    public bool Release(long milliseconds, bool inside)
    {
        bool click = pressed && !moved && inside && milliseconds - at < 500;
        Cancel(); return click;
    }
    public void Cancel() { pressed = false; moved = false; }
}
