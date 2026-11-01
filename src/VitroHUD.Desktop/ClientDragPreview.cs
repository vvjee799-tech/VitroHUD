using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;
using ShapePath = Microsoft.UI.Xaml.Shapes.Path;

namespace VitroHUD.Desktop;

// A passive, owned glass window carries the tab beyond the owner's HWND.
// Pointer capture stays on the original button; this surface never activates
// or receives input. Both surfaces return to the main window after docking.
internal sealed class ClientDragPreview : Window
{
    private readonly Canvas root = new() { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), IsHitTestVisible = false };
    private readonly ShapePath fill = new() { IsHitTestVisible = false };
    private readonly ShapePath rim = new() { IsHitTestVisible = false, StrokeThickness = 1 };
    private readonly FontIcon icon = new() { FontFamily = new FontFamily("Segoe Fluent Icons"), Glyph = "\uE9E9", FontSize = 15, Width = 28, Height = 56, IsHitTestVisible = false };
    private readonly GlassBackdrop backdrop = new();
    private readonly NativeWindow.RoundedRegion region = new();
    private readonly NativeWindow.SubclassProc procedure;
    private readonly nint previewHwnd;
    private bool shown, closed;

    internal ClientDragPreview(nint owner)
    {
        Title = "VitroHUD · 拖动预览"; Content = root; ExtendsContentIntoTitleBar = true;
        root.Children.Add(fill); root.Children.Add(rim); root.Children.Add(icon);
        previewHwnd = WindowNative.GetWindowHandle(this);
        var presenter = OverlappedPresenter.Create(); presenter.SetBorderAndTitleBar(false, false); presenter.IsResizable = false;
        presenter.IsMaximizable = false; presenter.IsMinimizable = false; AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;
        NativeWindow.SetWindowLongPtr(previewHwnd, -8, owner);
        NativeWindow.SetWindowLongPtr(previewHwnd, -20, (nint)(NativeWindow.GetWindowLongPtr(previewHwnd, -20).ToInt64() | 0x080000A0L));
        procedure = PreviewProcedure; NativeWindow.SetWindowSubclass(previewHwnd, procedure, 1, 0);
        NativeWindow.RemoveNonClientFrame(previewHwnd);
        var none = 1; NativeWindow.DwmSetWindowAttribute(previewHwnd, 33, ref none, sizeof(int));
        var noBorder = -2; NativeWindow.DwmSetWindowAttribute(previewHwnd, 34, ref noBorder, sizeof(int));
        backdrop.Attach(previewHwnd); backdrop.SetContour(0, 0, null, includeBody: false); SystemBackdrop = backdrop;
        Closed += (_, _) => { closed = true; SystemBackdrop = null; NativeWindow.RemoveWindowSubclass(previewHwnd, procedure, 1); };
    }
    internal void ShowOutline(IReadOnlyList<GlassPoint> points, double x, double y, double? shore,
        PointInt32 ownerPosition, double ownerScale, Brush fillBrush, Brush rimBrush, Brush foreground, double clarity, double frost)
    {
        var bounds = HandlePreviewBounds.Around(points);
        var left = ownerPosition.X + (int)Math.Floor(bounds.X * ownerScale);
        var top = ownerPosition.Y + (int)Math.Floor(bounds.Y * ownerScale);
        var width = Math.Max(1, (int)Math.Ceiling(bounds.Width * ownerScale));
        var height = Math.Max(1, (int)Math.Ceiling(bounds.Height * ownerScale));
        if (AppWindow.Position.X != left || AppWindow.Position.Y != top || AppWindow.Size.Width != width || AppWindow.Size.Height != height)
            AppWindow.MoveAndResize(new RectInt32(left, top, width, height));
        var previewScale = NativeWindow.GetDpiForWindow(previewHwnd) / 96d;
        var ratio = ownerScale / previewScale;
        var local = points.Select(p => new GlassPoint((ownerPosition.X + p.X * ownerScale - left) / previewScale,
            (ownerPosition.Y + p.Y * ownerScale - top) / previewScale)).ToArray();
        fill.Data = MainWindow.OutlineGeometry(local);
        rim.Data = MainWindow.OutlineGeometry(local, shore is { } edge ? (ownerPosition.X + edge * ownerScale - left) / previewScale : null);
        fill.Fill = fillBrush; rim.Stroke = rimBrush; icon.Foreground = foreground;
        icon.Width = 28 * ratio; icon.Height = 56 * ratio; icon.FontSize = 15 * ratio;
        Canvas.SetLeft(icon, (ownerPosition.X + x * ownerScale - left) / previewScale - icon.Width / 2);
        Canvas.SetTop(icon, (ownerPosition.Y + y * ownerScale - top) / previewScale - icon.Height / 2);
        var dpi = NativeWindow.GetDpiForWindow(previewHwnd);
        region.Update(previewHwnd, width, height, dpi, 0, 0, local, includeBody: false);
        backdrop.SetContour(0, 0, local, includeBody: false); backdrop.Apply(clarity, frost);
        if (!shown) { AppWindow.Show(false); shown = true; NativeWindow.Topmost(previewHwnd, true); }
        backdrop.Sync();
    }
    internal void HidePreview()
    {
        if (!shown) return;
        backdrop.Hide(); AppWindow.Hide(); shown = false;
    }
    internal void ClosePreview() { if (!closed) { HidePreview(); Close(); } }
    private nint PreviewProcedure(nint h, uint message, nint w, nint l, nuint id, nuint data)
    {
        if (message == 0x24) { NativeWindow.SetMinimumSize(h, l, 1, 1); return 0; }
        if (message == 0x83) return 0;
        if (message == 0x21) return 3; // MA_NOACTIVATE
        if (message == 0x84) return -1; // HTTRANSPARENT
        if (message == 0x7C && w.ToInt64() == -16) NativeWindow.SuppressFrameStyle(l);
        if (backdrop.CustomEffects && message == 0x14 && NativeWindow.ClearBackground(h, w)) return 1;
        if (backdrop.CustomEffects && message == 0x31E) NativeWindow.EnableAlpha(h);
        return NativeWindow.DefSubclassProc(h, message, w, l);
    }
}
