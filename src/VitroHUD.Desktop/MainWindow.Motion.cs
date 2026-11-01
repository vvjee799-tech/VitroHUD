using System.Diagnostics;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace VitroHUD.Desktop;

public sealed partial class MainWindow
{
    private const int CompactWidth = 76, CompactHeight = 44;
    private bool collapsed, compactAnimating;
    private RectInt32 expandedBounds, compactBounds;
    private double compactProgress, compactContentOpacity = 1;
    private CompactTimeline? compactTimeline;
    private long compactStarted;
    private readonly CompactDragGesture compactDrag = new();
    private RectInt32 compactDragOrigin;
    private uint compactDragPointer;

    private void InitializeCompactWidget()
    {
        CollapsedWidget.ManipulationMode = ManipulationModes.None;
        CollapsedWidget.Pressed = CompactPressed; CollapsedWidget.Moved = CompactMoved;
        CollapsedWidget.Released = CompactReleased;
        CollapsedWidget.Cancelled = e => { if (e.Pointer.PointerId == compactDragPointer) CancelCompactDrag(); };
    }
    private void CompactPressed(PointerRoutedEventArgs e)
    {
        if (!collapsed || compactAnimating || compactDrag.Pressed) return;
        if (!TryCompactScreenPoint(e, out var pointer) || !CollapsedWidget.CapturePointer(e.Pointer)) return;
        CollapsedWidget.Focus(FocusState.Pointer); compactDragPointer = e.Pointer.PointerId;
        compactDragOrigin = new(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
        compactDrag.Begin(pointer.X, pointer.Y, compactDragOrigin.X, compactDragOrigin.Y, NativeWindow.GetDpiForWindow(hwnd) / 96d);
        ToolTipService.SetToolTip(CollapsedWidget, null);
    }
    private bool TryCompactScreenPoint(PointerRoutedEventArgs e, out PointInt32 screen)
    {
        var point = e.GetCurrentPoint(Root).Position;
        var scale = NativeWindow.GetDpiForWindow(hwnd) / 96d;
        return NativeWindow.TryScreenPoint(hwnd, (int)Math.Round(point.X * scale), (int)Math.Round(point.Y * scale), out screen);
    }
    private void MoveCompactPointer(PointerRoutedEventArgs e)
    {
        if (!TryCompactScreenPoint(e, out var pointer)) return;
        var next = compactDrag.Move(pointer.X, pointer.Y);
        if (next is not { } position) return;
        var x = (int)Math.Round(position.X); var y = (int)Math.Round(position.Y);
        if (AppWindow.Position.X == x && AppWindow.Position.Y == y) return;
        AppWindow.Move(new PointInt32(x, y));
        compactBounds = new(x, y, AppWindow.Size.Width, AppWindow.Size.Height); SyncWindowGeometry();
    }
    private void CompactMoved(PointerRoutedEventArgs e)
    {
        if (!compactDrag.Pressed || e.Pointer.PointerId != compactDragPointer) return;
        if (!e.GetCurrentPoint(Root).Properties.IsLeftButtonPressed) { CancelCompactDrag(); return; }
        MoveCompactPointer(e); e.Handled = true;
    }
    private void CompactReleased(PointerRoutedEventArgs e)
    {
        if (!compactDrag.Pressed || e.Pointer.PointerId != compactDragPointer) return;
        MoveCompactPointer(e);
        var point = e.GetCurrentPoint(CollapsedWidget).Position;
        var inside = point.X >= 0 && point.Y >= 0 && point.X <= CollapsedWidget.ActualWidth && point.Y <= CollapsedWidget.ActualHeight;
        var moved = compactDrag.Dragged; var expand = compactDrag.End(inside);
        CollapsedWidget.ReleasePointerCaptures(); ToolTipService.SetToolTip(CollapsedWidget, "单击展开 · 按住拖动");
        if (moved)
        {
            compactBounds = ConstrainCompactBounds(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
            AppWindow.Move(new PointInt32(compactBounds.X, compactBounds.Y)); SyncWindowGeometry();
            settings.CompactWidgetX = compactBounds.X; settings.CompactWidgetY = compactBounds.Y; ScheduleSave();
        }
        if (expand) Restore();
    }
    private void CancelCompactDrag()
    {
        if (!compactDrag.Pressed) return;
        compactDrag.End(false); CollapsedWidget.ReleasePointerCaptures();
        compactBounds = compactDragOrigin; AppWindow.Move(new PointInt32(compactBounds.X, compactBounds.Y)); SyncWindowGeometry();
        ToolTipService.SetToolTip(CollapsedWidget, "单击展开 · 按住拖动");
    }
    private static RectInt32 ConstrainCompactBounds(int x, int y, int width, int height)
    {
        var area = DisplayArea.GetFromPoint(new PointInt32(x + width / 2, y + height / 2), DisplayAreaFallback.Nearest).WorkArea;
        return new(Math.Clamp(x, area.X, Math.Max(area.X, area.X + area.Width - width)),
            Math.Clamp(y, area.Y, Math.Max(area.Y, area.Y + area.Height - height)), width, height);
    }

    private void Collapse()
    {
        if (!ready || collapsed) return;
        modelFlyout?.Hide(); clientFlyout?.Hide(); Root.ContextFlyout.Hide(); CancelClientGesture(); clientPreview?.HidePreview();
        if (!compactAnimating)
        {
            expandedBounds = new(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
            var scale = NativeWindow.GetDpiForWindow(hwnd) / 96d;
            var width = (int)Math.Round(CompactWidth * scale); var height = (int)Math.Round(CompactHeight * scale);
            var x = expandedBounds.X + expandedBounds.Width - (int)Math.Round(ClientHandlePlacement.Gutter * scale) - width;
            var y = expandedBounds.Y + expandedBounds.Height - height;
            compactBounds = ConstrainCompactBounds(settings.CompactWidgetX ?? x, settings.CompactWidgetY ?? y, width, height);
        }
        collapsed = true; NativeWindow.PassThrough(hwnd, false); NativeWindow.Topmost(hwnd, true);
        Title = "VitroHUD · 点击展开"; BeginCompactAnimation(1);
    }
    private void ExpandWidget_Click(object sender, RoutedEventArgs e) => Restore();
    internal void Restore()
    {
        CancelCompactDrag();
        NativeWindow.PassThrough(hwnd, false);
        if (collapsed)
        {
            var area = DisplayArea.GetFromPoint(new PointInt32(expandedBounds.X + expandedBounds.Width / 2,
                expandedBounds.Y + expandedBounds.Height / 2), DisplayAreaFallback.Nearest).WorkArea;
            expandedBounds.Width = Math.Min(expandedBounds.Width, area.Width); expandedBounds.Height = Math.Min(expandedBounds.Height, area.Height);
            expandedBounds.X = Math.Clamp(expandedBounds.X, area.X, Math.Max(area.X, area.X + area.Width - expandedBounds.Width));
            expandedBounds.Y = Math.Clamp(expandedBounds.Y, area.Y, Math.Max(area.Y, area.Y + area.Height - expandedBounds.Height));
            collapsed = false; Title = "VitroHUD · 官方会话投射"; BeginCompactAnimation(0);
        }
        AppWindow.Show(); Activate(); NativeWindow.SetForegroundWindow(hwnd); SyncWindowGeometry();
    }
    private void BeginCompactAnimation(double target)
    {
        compactTimeline = new CompactTimeline(compactProgress, target, compactContentOpacity);
        compactStarted = Stopwatch.GetTimestamp();
        if (!compactAnimating) { compactAnimating = true; CompositionTarget.Rendering += AnimateCompact; }
        var scale = NativeWindow.GetDpiForWindow(hwnd) / 96d;
        ExpandedContent.Width = expandedBounds.Width / scale; ExpandedContent.Height = expandedBounds.Height / scale;
        ExpandedContent.HorizontalAlignment = HorizontalAlignment.Right; ExpandedContent.VerticalAlignment = VerticalAlignment.Bottom;
        ExpandedContent.IsHitTestVisible = false;
        CollapsedWidget.Visibility = Visibility.Visible; ClientHandleLayer.Visibility = Visibility.Collapsed; WindowDragHandle.Visibility = Visibility.Collapsed;
        ShellRim.Visibility = EdgeHighlightLayer.Visibility = Visibility.Visible;
        if (!motionSettings.AnimationsEnabled) { compactProgress = target; compactContentOpacity = target == 0 ? 1 : 0; PaintCompactFrame(); CompleteCompactAnimation(); }
        else PaintCompactFrame();
    }
    private void AnimateCompact(object? sender, object e)
    {
        var elapsed = (Stopwatch.GetTimestamp() - compactStarted) * 1000d / Stopwatch.Frequency;
        if (compactTimeline == null) return;
        var frame = compactTimeline.Sample(motionSettings.AnimationsEnabled ? elapsed : double.MaxValue);
        compactProgress = frame.Progress; compactContentOpacity = frame.Opacity;
        PaintCompactFrame(); if (frame.Complete) CompleteCompactAnimation();
    }
    private void PaintCompactFrame()
    {
        var p = compactProgress;
        var bounds = new RectInt32((int)Math.Round(GlassMotion.Lerp(expandedBounds.X, compactBounds.X, p)),
            (int)Math.Round(GlassMotion.Lerp(expandedBounds.Y, compactBounds.Y, p)),
            (int)Math.Round(GlassMotion.Lerp(expandedBounds.Width, compactBounds.Width, p)),
            (int)Math.Round(GlassMotion.Lerp(expandedBounds.Height, compactBounds.Height, p)));
        var inset = ClientHandlePlacement.Gutter * (1 - p);
        TintLayer.Margin = ShellRim.Margin = EdgeHighlightLayer.Margin = new Thickness(inset, 0, inset, 0);
        ExpandedContent.Opacity = compactContentOpacity;
        ExpandedContent.Visibility = compactContentOpacity > .001 ? Visibility.Visible : Visibility.Collapsed;
        CollapsedWidget.Opacity = GlassMotion.Smooth((p - .58) / .42);
        CollapsedWidget.IsHitTestVisible = p > .58;
        if (AppWindow.Position.X != bounds.X || AppWindow.Position.Y != bounds.Y || AppWindow.Size.Width != bounds.Width || AppWindow.Size.Height != bounds.Height)
            AppWindow.MoveAndResize(bounds);
        SyncWindowGeometry();
    }
    private void CompleteCompactAnimation()
    {
        StopCompactAnimation();
        ExpandedContent.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        ExpandedContent.IsHitTestVisible = !collapsed;
        ExpandedContent.Width = double.NaN; ExpandedContent.Height = double.NaN;
        ExpandedContent.HorizontalAlignment = HorizontalAlignment.Stretch; ExpandedContent.VerticalAlignment = VerticalAlignment.Stretch;
        CollapsedWidget.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
        ShellRim.Visibility = EdgeHighlightLayer.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
        ClientHandleLayer.Visibility = WindowDragHandle.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        if (!collapsed)
        {
            NativeWindow.Topmost(hwnd, settings.AlwaysOnTop); LayoutClientHandle();
            var scale = NativeWindow.GetDpiForWindow(hwnd) / 96d;
            settings.X = AppWindow.Position.X + (int)Math.Round(ClientHandlePlacement.Gutter * scale); settings.Y = AppWindow.Position.Y;
            settings.Width = (int)Math.Round(AppWindow.Size.Width / scale - 2 * ClientHandlePlacement.Gutter); settings.Height = (int)Math.Round(AppWindow.Size.Height / scale);
            ScheduleSave();
        }
        SyncWindowGeometry();
    }
    private void StopCompactAnimation() { CompositionTarget.Rendering -= AnimateCompact; compactAnimating = false; CancelCompactDrag(); }
}
