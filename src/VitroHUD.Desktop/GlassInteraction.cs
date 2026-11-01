namespace VitroHUD.Desktop;

internal readonly record struct GlassPoint(double X, double Y);

// Screen coordinates stay stable while moving the HWND under the pointer.
// Dragged is latched until release so returning to the start cannot expand it.
internal sealed class CompactDragGesture
{
    internal bool Pressed { get; private set; }
    internal bool Dragged { get; private set; }
    private double pressX, pressY, windowX, windowY, thresholdSquared;
    internal void Begin(double x, double y, double originX, double originY, double scale)
    {
        pressX = x; pressY = y; windowX = originX; windowY = originY;
        thresholdSquared = Math.Pow(5 * scale, 2); Pressed = true; Dragged = false;
    }
    internal GlassPoint? Move(double x, double y)
    {
        if (!Pressed) return null;
        var dx = x - pressX; var dy = y - pressY;
        if (!Dragged && dx * dx + dy * dy < thresholdSquared) return null;
        Dragged = true; return new GlassPoint(windowX + dx, windowY + dy);
    }
    internal bool End(bool inside)
    {
        var click = Pressed && !Dragged && inside;
        Pressed = false; Dragged = false; return click;
    }
}

// Coordinates are DIPs. Persist normalized positions independently of DPI and
// window size; the invisible gutters belong to the native shape, not the page.
internal sealed class ClientHandlePlacement
{
    internal const double Gutter = 32, HalfWidth = 14, HalfHeight = 28;
    internal double X { get; private set; }
    internal double Y { get; private set; }
    internal int Dock { get; private set; } = 1;
    internal bool Pressed { get; private set; }
    internal bool Dragged { get; private set; }
    private double pressX, pressY, originX, originY;
    private int originDock;
    internal static double Unit(double value, double fallback) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : fallback;
    internal static double MinY => 54;
    internal static double MaxY(double height) => Math.Max(MinY, height - 70);
    internal static double Left => Gutter;
    internal static double Right(double width) => Math.Max(Left, width - Gutter);
    internal static double DockX(int dock, double width) => dock < 0 ? Left - 12 : Right(width) + 12;
    internal void Load(double nx, double ny, int dock, double width, double height)
    {
        Dock = Math.Clamp(dock, -1, 1);
        X = Dock == 0 ? Gutter + Unit(nx, 1) * Math.Max(0, width - 2 * Gutter) : DockX(Dock, width);
        Y = MinY + Unit(ny, .46) * (MaxY(height) - MinY);
        Clamp(width, height);
        if (Dock == 0) Snap(width);
    }
    internal (double X, double Y) Normalized(double width, double height) =>
        (Unit((X - Gutter) / Math.Max(1, width - 2 * Gutter), 1), Unit((Y - MinY) / Math.Max(1, MaxY(height) - MinY), .46));
    internal void Begin(double x, double y)
    {
        Pressed = true; Dragged = false; pressX = x; pressY = y;
        originX = X; originY = Y; originDock = Dock;
    }
    internal void Move(double x, double y, double width, double height)
    {
        if (!Pressed) return;
        var dx = x - pressX; var dy = y - pressY;
        if (!Dragged && dx * dx + dy * dy < 25) return;
        Dragged = true; Dock = 0; X = originX + dx; Y = originY + dy;
    }
    internal bool End(double width, double height, bool cancel = false)
    {
        if (!Pressed) return false;
        var click = !Dragged && !cancel;
        if (cancel) { X = originX; Y = originY; Dock = originDock; }
        else if (Dragged) Snap(width);
        Pressed = false; Dragged = false; Clamp(width, height); return click;
    }
    internal void Nudge(double dx, double dy, int dock, double width, double height)
    {
        if (dock == 0 && dx == 0) { Y += dy; Clamp(width, height); return; }
        Dock = dock;
        if (dock != 0) X = DockX(dock, width);
        else { Dock = dx < 0 ? -1 : 1; X = DockX(Dock, width); Y += dy; Clamp(width, height); }
    }
    private void Snap(double width)
    {
        var left = Math.Abs(X - Left); var right = Math.Abs(X - Right(width));
        Dock = left < right ? -1 : 1;
        X = DockX(Dock, width);
    }
    private void Clamp(double width, double height)
    {
        X = Math.Clamp(X, HalfWidth + 2, Math.Max(HalfWidth + 2, width - HalfWidth - 2));
        Y = Math.Clamp(Y, MinY, MaxY(height));
    }
    internal static (int Side, double Amount) Connection(double x, double width)
    {
        var dl = Math.Abs(x - Left); var dr = Math.Abs(x - Right(width));
        var d = Math.Min(dl, dr);
        return (dl < dr ? -1 : 1, 1 - GlassMotion.Smooth((d - 14) / 48));
    }
}

internal readonly record struct HandlePreviewBounds(double X, double Y, double Width, double Height)
{
    internal static bool IsOutside(IReadOnlyList<GlassPoint> outline, double width, double height) =>
        outline.Any(p => p.X < 1 || p.Y < 1 || p.X > width - 1 || p.Y > height - 1);
    internal static HandlePreviewBounds Around(IReadOnlyList<GlassPoint> outline)
    {
        const double padding = 4;
        var x = Math.Floor(outline.Min(p => p.X)) - padding;
        var y = Math.Floor(outline.Min(p => p.Y)) - padding;
        return new(x, y, Math.Ceiling(outline.Max(p => p.X)) + padding - x,
            Math.Ceiling(outline.Max(p => p.Y)) + padding - y);
    }
}

internal static class GlassMotion
{
    internal static double Smooth(double t) { t = Math.Clamp(t, 0, 1); return t * t * (3 - 2 * t); }
    internal static double Lerp(double a, double b, double t) => a + (b - a) * t;
}

internal sealed class CompactTimeline(double from, double target, double startingOpacity)
{
    private readonly double fadeOut = target == 1 && from < .00001 ? 60 * startingOpacity : 0;
    private readonly double morph = (target == 1 ? 220 : 240) * Math.Abs(target - from);
    private readonly double opacityFrom = from < .00001 ? startingOpacity : 0;
    internal double Duration => fadeOut + morph + (target == 0 ? 80 * (1 - opacityFrom) : 0);
    internal (double Progress, double Opacity, bool Complete) Sample(double elapsed)
    {
        if (elapsed >= Duration) return (target, target == 0 ? 1 : 0, true);
        if (target == 1)
        {
            if (elapsed < fadeOut) return (from, startingOpacity * (1 - GlassMotion.Smooth(elapsed / fadeOut)), false);
            return (GlassMotion.Lerp(from, target, GlassMotion.Smooth((elapsed - fadeOut) / Math.Max(1, morph))), 0, false);
        }
        if (elapsed < morph) return (GlassMotion.Lerp(from, 0, GlassMotion.Smooth(elapsed / Math.Max(1, morph))), 0, false);
        return (0, GlassMotion.Lerp(opacityFrom, 1, GlassMotion.Smooth((elapsed - morph) / Math.Max(1, Duration - morph))), false);
    }
}

// A narrow, rounded vertical tab throughout. Only the side nearest the shore
// grows a liquid neck; it never morphs into a circle. The same sampled outline
// clips both native windows and paints the glass so their silhouettes agree.
internal static class LiquidHandleGeometry
{
    internal static List<GlassPoint> Outline(double x, double y, double edge, double connection)
    {
        var mirror = edge > x ? -1d : 1d;
        var shore = (edge - x) * mirror;
        const double l = -14, r = 14, t = -28, b = 28, cr = 10;
        var points = new List<GlassPoint>(90);
        GlassPoint current = default;
        void Line(double px, double py) { current = new(px, py); points.Add(new(x + mirror * px, y + py)); }
        void Curve(double ax, double ay, double bx, double by, double ex, double ey)
        {
            var start = current;
            for (var i = 1; i <= 8; i++)
            {
                var u = i / 8d; var v = 1 - u;
                var px = v * v * v * start.X + 3 * v * v * u * ax + 3 * v * u * u * bx + u * u * u * ex;
                var py = v * v * v * start.Y + 3 * v * v * u * ay + 3 * v * u * u * by + u * u * u * ey;
                points.Add(new(x + mirror * px, y + py));
            }
            current = new(ex, ey);
        }
        Line(l + cr, t); Line(r - cr, t); Curve(r - 4, t, r, t + 4, r, t + cr);
        Line(r, b - cr); Curve(r, b - 4, r - 4, b, r - cr, b);
        Line(l + cr, b);
        if (connection > .005)
        {
            var near = GlassMotion.Lerp(l, l + cr, connection);
            var cornerY = GlassMotion.Lerp(b - cr, b, connection);
            var cornerX = GlassMotion.Lerp(l + 4, l + cr, connection);
            Curve(cornerX, b, near, cornerY, near, cornerY);
            var join = 28 * connection;
            var flare = 42 * connection;
            var tension = Math.Max(2, Math.Abs(near - shore)) * .48;
            Line(near, join);
            Curve(near - tension, join, shore, flare - 12 * connection, shore, flare);
            Line(shore, -flare);
            Curve(shore, -flare + 12 * connection, near - tension, -join, near, -join);
            Line(near, -cornerY);
            Curve(near, -cornerY, cornerX, t, l + cr, t);
        }
        else
        {
            Curve(l + 4, b, l, b - 4, l, b - cr);
            Line(l, t + cr); Curve(l, t + 4, l + 4, t, l + cr, t);
        }
        return points;
    }
}
