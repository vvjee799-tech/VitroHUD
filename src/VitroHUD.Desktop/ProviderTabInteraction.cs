namespace VitroHUD.Desktop;

// Gesture decisions are independent of rendering, so interrupted drags and holds
// can be checked without moving the user's desktop pointer.
internal sealed class ProviderTabInteraction
{
    // Overlap the 1px outlines so adjacent tabs share one seam.
    internal const double Gap = -1, MoveThreshold = 8;
    internal const int HoldMilliseconds = 520;
    internal List<string> Order { get; } = ["Claude", "ChatGPT", "DeepSeek"];
    internal string? CollapsedProvider { get; private set; }
    internal string? Pressed { get; private set; }
    internal bool Moved { get; private set; }
    internal bool Held { get; private set; }
    internal bool Dragging => Pressed != null && Moved && !Held && CollapsedProvider == null;
    internal double DragX { get; private set; }
    private double startX, startY, startLeft;
    private string[] originalOrder = [];

    internal static double TabWidth(double availableWidth) => Math.Max(0, (availableWidth - 2 * Gap) / 3);
    internal double Left(string provider, double width) => (CollapsedProvider != null ? 1 : Order.IndexOf(provider)) * (width + Gap);

    internal void Begin(string provider, double x, double y, double currentLeft)
    {
        Pressed = provider; startX = x; startY = y; startLeft = DragX = currentLeft;
        Moved = Held = false; originalOrder = Order.ToArray();
    }

    internal void Move(double x, double y, double width)
    {
        if (Pressed == null || Held) return;
        if (Math.Abs(x - startX) > MoveThreshold || Math.Abs(y - startY) > MoveThreshold) Moved = true;
        if (!Dragging) return;
        DragX = Math.Clamp(startLeft + x - startX, 0, 2 * (width + Gap));
        var index = Math.Clamp((int)Math.Round(DragX / (width + Gap)), 0, 2);
        var previous = Order.IndexOf(Pressed);
        if (index == previous) return;
        Order.RemoveAt(previous); Order.Insert(index, Pressed);
    }

    internal bool Hold()
    {
        if (Pressed == null || Moved || Held) return false;
        Held = true;
        Toggle(Pressed);
        return true;
    }

    internal void Toggle(string provider) => CollapsedProvider = CollapsedProvider == null ? provider : null;

    internal void Nudge(string provider, int direction)
    {
        if (CollapsedProvider != null) return;
        var index = Order.IndexOf(provider);
        var target = Math.Clamp(index + direction, 0, 2);
        Order.RemoveAt(index); Order.Insert(target, provider);
    }

    internal string? End(bool inside, bool cancel = false)
    {
        var clicked = !cancel && inside && !Moved && !Held ? Pressed : null;
        if (cancel && Dragging) { Order.Clear(); Order.AddRange(originalOrder); }
        Pressed = null; Moved = Held = false;
        return clicked;
    }
}
