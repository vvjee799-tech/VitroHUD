using VitroHUD.Desktop;

var count = 0;
void Check(bool value, string description) { count++; if (!value) throw new Exception(description); }
void Near(double actual, double expected, string description) => Check(Math.Abs(actual - expected) < .00001, description);
const double w = 434, h = 809;
var placement = new ClientHandlePlacement();
placement.Load(1, .46, 1, w, h);
Near(placement.X, 414, "right tab extends past panel edge");
placement.Begin(placement.X, placement.Y); placement.Move(placement.X + 2, placement.Y + 1, w, h);
Check(placement.End(w, h), "tiny pointer jitter still clicks");
placement.Begin(placement.X, placement.Y); placement.Move(220, 300, w, h);
Check(placement.Dock == 0, "middle remains free while dragging"); Near(placement.X, 220, "pointer movement follows origin");
Check(!placement.End(w, h), "drag does not click"); Check(placement.Dock == 1, "release in right half always docks");
Near(placement.X, 414, "release from middle targets right edge");
placement.Begin(placement.X, placement.Y); placement.Move(34, 280, w, h); placement.End(w, h);
Check(placement.Dock == -1, "release near left edge docks"); Near(placement.X, 20, "left tab is outside body");
placement.Begin(20, placement.Y); placement.Move(220, 500, w, h); placement.End(w, h, true);
Check(placement.Dock == -1, "cancel restores original docking"); Near(placement.X, 20, "cancel restores X");
placement.Begin(20, placement.Y); placement.Move(-999, -999, w, h); placement.End(w, h);
Check(placement.X >= 16 && placement.Y >= 54, "cannot lose control above or left of window");
placement.Begin(placement.X, placement.Y); placement.Move(9999, 9999, w, h); placement.End(w, h);
Check(placement.X <= w - 16 && placement.Y <= h - 70, "cannot lose control below or right of window");
placement.Load(.37, .62, 0, w, h);
Check(placement.Dock == -1, "legacy floating positions load at the nearer edge");
var saved = placement.Normalized(w, h);
var resized = new ClientHandlePlacement(); resized.Load(saved.X, saved.Y, placement.Dock, 664, 1100);
var restored = resized.Normalized(664, 1100); Check(resized.Dock == -1 && resized.X == 20, "docked edge survives resize"); Near(restored.Y, .62, "normalized Y survives resize");
resized.Load(double.NaN, double.PositiveInfinity, 99, w, h);
Check(double.IsFinite(resized.X) && double.IsFinite(resized.Y) && resized.Dock == 1, "corrupt saved values are sanitized");
Check(ClientHandlePlacement.Connection(w / 2, w).Amount == 0, "no neck in the center");
Check(ClientHandlePlacement.Connection(414, w).Amount == 1, "neck fused at right edge");
Check(ClientHandlePlacement.Connection(20, w).Amount == 1, "neck fused at left edge");
placement.Load(1, .46, 1, w, h); placement.Nudge(-10, 0, 0, w, h);
Check(placement.Dock == -1 && placement.X == 20, "keyboard movement keeps the handle attached");
placement.Nudge(0, 0, -1, w, h); var oldY = placement.Y; placement.Nudge(0, 10, 0, w, h);
Check(placement.Dock == -1 && placement.X == 20 && placement.Y == oldY + 10, "vertical keyboard move preserves attachment");
foreach (var width in new[] { 434d, 664d })
{
    foreach (var fraction in new[] { .2, .4, .49, .5, .51, .6, .8 })
    {
        placement.Load(1, .46, 1, width, h); placement.Begin(placement.X, placement.Y);
        var targetX = ClientHandlePlacement.Gutter + fraction * (width - 2 * ClientHandlePlacement.Gutter);
        placement.Move(targetX, 360, width, h);
        Check(placement.Dock == 0, "drag remains free away from either edge"); Near(placement.X, targetX, "free drag follows release position");
        Check(!placement.End(width, h), "auto-docking never opens client menu");
        var expectedDock = fraction < .5 ? -1 : 1;
        Check(placement.Dock == expectedDock, "release chooses the nearest edge, with midpoint going right");
        Near(placement.X, ClientHandlePlacement.DockX(expectedDock, width), "release reaches exact dock coordinate");
        Near(placement.Y, 360, "auto-docking preserves release height");
    }
}
var free = LiquidHandleGeometry.Outline(220, 300, 402, 0);
Near(free.Max(p => p.X) - free.Min(p => p.X), 28, "detached entrance remains narrow");
Near(free.Max(p => p.Y) - free.Min(p => p.Y), 56, "detached entrance remains vertical, not circular");
var right = LiquidHandleGeometry.Outline(414, 300, 402, 1);
var left = LiquidHandleGeometry.Outline(20, 300, 32, 1);
Check(right.Count == left.Count, "both docking sides share contour topology");
for (var i = 0; i < right.Count; i++) { Near(right[i].X + left[i].X, w, "left/right contour symmetry"); Near(right[i].Y, left[i].Y, "left/right contour vertical symmetry"); }
foreach (var x in new[] { 16d, 20, 32, 60, 110, 220, 340, 390, 402, 414, 418 })
{
    var c = ClientHandlePlacement.Connection(x, w);
    var outline = LiquidHandleGeometry.Outline(x, 300, c.Side < 0 ? 32 : 402, c.Amount);
    Check(outline.All(p => double.IsFinite(p.X) && double.IsFinite(p.Y) && p.X >= 0 && p.X <= w), "neck stays within native envelope");
}
var previous = 0d;
for (var i = 0; i <= 100; i++) { var eased = GlassMotion.Smooth(i / 100d); Check(eased >= previous && eased <= 1, "transition never overshoots"); previous = eased; }
Near(GlassMotion.Smooth(0), 0, "animation exact start"); Near(GlassMotion.Smooth(1), 1, "animation exact end");
var interrupted = GlassMotion.Lerp(0, 1, GlassMotion.Smooth(.43));
Near(GlassMotion.Lerp(interrupted, 0, GlassMotion.Smooth(0)), interrupted, "reversal starts from current shape");
foreach (var direction in new[] { 0d, 1d })
{
    var timeline = new CompactTimeline(1 - direction, direction, direction);
    Near(timeline.Duration, direction == 1 ? 280 : 320, "native transition duration");
    for (var ms = 0; ms <= timeline.Duration; ms += 5)
    {
        var sample = timeline.Sample(ms);
        Check(sample.Opacity == 0 || sample.Progress == 0, "text is never visible inside a partially resized window");
    }
    var end = timeline.Sample(10000); Near(end.Progress, direction, "exact final bounds"); Check(end.Complete, "timeline completes");
    var middle = timeline.Sample(130);
    var reverse = new CompactTimeline(middle.Progress, 1 - direction, middle.Opacity);
    Near(reverse.Sample(0).Progress, middle.Progress, "reversal preserves current native bounds");
}
var partialFade = new CompactTimeline(0, 0, .4);
Near(partialFade.Sample(0).Opacity, .4, "reversal during initial fade preserves opacity");
foreach (var point in new[] { new GlassPoint(-180, -120), new GlassPoint(w + 240, h + 180), new GlassPoint(-200, 300), new GlassPoint(w + 200, 300) })
{
    placement.Load(1, .46, 1, w, h); placement.Begin(placement.X, placement.Y);
    placement.Move(point.X, point.Y, w, h);
    Near(placement.X, point.X, "held entrance can move past horizontal window bounds");
    Near(placement.Y, point.Y, "held entrance can move past vertical window bounds");
    Check(placement.Dock == 0, "outside drag remains free until release");
    var outside = LiquidHandleGeometry.Outline(placement.X, placement.Y, point.X < w / 2 ? 32 : 402, 0);
    Check(HandlePreviewBounds.IsOutside(outside, w, h), "exterior silhouette requests a separate native preview");
    var preview = HandlePreviewBounds.Around(outside);
    Check(outside.All(p => p.X >= preview.X + 3 && p.Y >= preview.Y + 3 && p.X <= preview.X + preview.Width - 3 && p.Y <= preview.Y + preview.Height - 3), "preview encloses complete shape with antialiasing margin");
    Check(preview.Width <= 38 && preview.Height <= 66, "far exterior preview does not create a desktop-sized surface");
    Check(!placement.End(w, h), "release from outside does not open menu");
    Check(placement.Dock == (point.X < w / 2 ? -1 : 1), "release from outside docks to nearest side");
    Near(placement.Y, Math.Clamp(point.Y, 54, h - 70), "outside release clamps height only after releasing");
}
Check(!HandlePreviewBounds.IsOutside(LiquidHandleGeometry.Outline(220, 300, 402, 0), w, h), "interior entrance uses original window surface");
Check(!HandlePreviewBounds.IsOutside(LiquidHandleGeometry.Outline(414, 300, 402, 1), w, h), "resting dock returns to original window surface");
var capsule = new CompactDragGesture();
capsule.Begin(150, 200, 100, 180, 1.5);
Check(capsule.Move(154, 204) == null, "DPI-scaled jitter is still a capsule click");
Check(capsule.End(true), "single capsule click expands");
Check(!capsule.End(true), "duplicate release cannot expand again");
capsule.Begin(150, 200, 100, 180, 1.5);
var movedCapsule = capsule.Move(180, 170)!.Value;
Near(movedCapsule.X, 130, "capsule follows screen X delta"); Near(movedCapsule.Y, 150, "capsule follows screen Y delta");
var returnedCapsule = capsule.Move(150, 200)!.Value;
Near(returnedCapsule.X, 100, "return drag to its origin");
Check(!capsule.End(true), "dragging back to origin still does not expand");
Check(capsule.Move(900, 900) == null, "late pointer motion after release is ignored");
capsule.Begin(-1300, 100, -1320, 80, 1);
var negativeDisplay = capsule.Move(-1400, 130)!.Value;
Near(negativeDisplay.X, -1420, "negative monitor coordinates are supported");
Check(!capsule.End(false), "cancel and capture loss do not expand");
capsule.Begin(0, 0, 0, 0, 1);
Check(capsule.Move(5, 0) != null, "crossing the drag threshold starts movement");
Check(!capsule.End(true), "quick drag release does not expand");
capsule.Begin(0, 0, 0, 0, 1);
Check(!capsule.End(false), "release outside without dragging does not expand");
Console.WriteLine($"Glass interaction checks passed: {count}");
