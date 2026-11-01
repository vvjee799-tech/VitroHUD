using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.ViewManagement;
using ShapePath = Microsoft.UI.Xaml.Shapes.Path;

namespace VitroHUD.Desktop;

public sealed partial class MainWindow
{
    private readonly UISettings motionSettings = new();
    private readonly ClientHandlePlacement clientPlacement = new();
    private readonly ShapePath clientGlass = new() { IsHitTestVisible = false };
    private readonly ShapePath clientRim = new() { IsHitTestVisible = false, StrokeThickness = 1 };
    private readonly ShapePath panelRim = new() { IsHitTestVisible = false, StrokeThickness = 1 };
    private ClientHandleButton clientHandle = null!;
    private Flyout? clientFlyout;
    private bool clientMenuOpen, clientAnimating, clientReady, clientHovered;
    private uint clientPointer;
    private long clientFrame;
    private double clientX, clientY, clientConnection;
    private List<GlassPoint>? clientOutline;
    private ClientDragPreview? clientPreview;
    private bool clientUsesPreview;

    private void InitializeClientHandle()
    {
        clientHandle = new ClientHandleButton
        {
            Width = 36, Height = 64, Padding = new Thickness(0), CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0),
            Content = new FontIcon { FontFamily = new FontFamily("Segoe Fluent Icons"), Glyph = "\uE9E9", FontSize = 15 },
            Style = (Style)Root.Resources["QuietButton"], ManipulationMode = ManipulationModes.None
        };
        foreach (var state in new[] { "PointerOver", "Pressed", "Disabled" })
            clientHandle.Resources["ButtonBackground" + state] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        clientHandle.Click += (_, _) => ShowClientMenu();
        clientHandle.Pressed = ClientPressed; clientHandle.Moved = ClientMoved;
        clientHandle.Released = ClientReleased; clientHandle.Cancelled = _ => CancelClientGesture();
        clientHandle.Shortcut = ClientShortcut;
        clientHandle.PointerEntered += (_, _) => { clientHovered = true; PaintClientHandle(); };
        clientHandle.PointerExited += (_, _) => { clientHovered = false; PaintClientHandle(); };
        ClientHandleLayer.Children.Add(panelRim); ClientHandleLayer.Children.Add(clientGlass);
        ClientHandleLayer.Children.Add(clientRim); ClientHandleLayer.Children.Add(clientHandle);
        Root.SizeChanged += (_, _) => { if (!compactAnimating && !collapsed) LayoutClientHandle(); };
        UpdateClientHandle();
    }

    private void LayoutClientHandle()
    {
        if (Root.ActualWidth < 100 || Root.ActualHeight < 100 || clientPlacement.Pressed) return;
        clientPlacement.Load(settings.ClientHandleX, settings.ClientHandleY, settings.ClientHandleDock, Root.ActualWidth, Root.ActualHeight);
        clientX = clientPlacement.X; clientY = clientPlacement.Y;
        clientConnection = ClientConnectionTarget();
        clientReady = true; PaintClientHandle();
    }
    private void UpdateClientHandle()
    {
        AutomationProperties.SetName(clientHandle, $"切换客户端，当前 {settings.Provider}");
        AutomationProperties.SetHelpText(clientHandle, "单击打开菜单；按住可在面板内外自由拖动，松手吸附到较近的左右边缘；上下键移动，左右键切换贴边。");
        ToolTipService.SetToolTip(clientHandle, $"{settings.Provider} · 点击切换，拖动移动");
    }
    private void ClientPressed(PointerRoutedEventArgs e)
    {
        if (compactAnimating || collapsed || clientPlacement.Pressed || !clientHandle.CapturePointer(e.Pointer)) return;
        clientFlyout?.Hide(); clientHandle.Focus(FocusState.Pointer);
        var p = e.GetCurrentPoint(ClientHandleLayer).Position; clientPointer = e.Pointer.PointerId;
        ToolTipService.SetToolTip(clientHandle, null);
        clientPlacement.Begin(p.X, p.Y); PaintClientHandle();
    }
    private void ClientMoved(PointerRoutedEventArgs e)
    {
        if (!clientPlacement.Pressed || e.Pointer.PointerId != clientPointer) return;
        var point = e.GetCurrentPoint(ClientHandleLayer);
        if (!point.Properties.IsLeftButtonPressed) { CancelClientGesture(); return; }
        clientPlacement.Move(point.Position.X, point.Position.Y, Root.ActualWidth, Root.ActualHeight);
        if (clientPlacement.Dragged)
        {
            clientX = clientPlacement.X; clientY = clientPlacement.Y;
            clientConnection = ClientConnectionTarget(); PaintClientHandle();
        }
        e.Handled = true;
    }
    private void ClientReleased(PointerRoutedEventArgs e)
    {
        if (!clientPlacement.Pressed || e.Pointer.PointerId != clientPointer) return;
        var point = e.GetCurrentPoint(ClientHandleLayer).Position;
        clientPlacement.Move(point.X, point.Y, Root.ActualWidth, Root.ActualHeight);
        if (clientPlacement.Dragged) { clientX = clientPlacement.X; clientY = clientPlacement.Y; }
        var click = clientPlacement.End(Root.ActualWidth, Root.ActualHeight);
        clientHandle.ReleasePointerCaptures();
        UpdateClientHandle();
        SaveClientPosition(); StartClientMotion();
        if (click) ShowClientMenu();
    }
    private void CancelClientGesture()
    {
        if (!clientPlacement.Pressed) return;
        clientPlacement.End(Root.ActualWidth, Root.ActualHeight, cancel: true);
        clientHandle.ReleasePointerCaptures(); UpdateClientHandle(); StartClientMotion();
    }
    private void SaveClientPosition()
    {
        var normalized = clientPlacement.Normalized(Root.ActualWidth, Root.ActualHeight);
        settings.ClientHandleDock = clientPlacement.Dock; settings.ClientHandleX = normalized.X; settings.ClientHandleY = normalized.Y;
        ScheduleSave();
    }
    private void ClientShortcut(KeyRoutedEventArgs e)
    {
        if (e.Key is not (VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down)) return;
        CancelClientGesture();
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
        var dock = ctrl && e.Key is VirtualKey.Left or VirtualKey.Right ? (e.Key == VirtualKey.Left ? -1 : 1) : 0;
        clientPlacement.Nudge(e.Key == VirtualKey.Left ? -10 : e.Key == VirtualKey.Right ? 10 : 0,
            e.Key == VirtualKey.Up ? -10 : e.Key == VirtualKey.Down ? 10 : 0, dock, Root.ActualWidth, Root.ActualHeight);
        SaveClientPosition(); StartClientMotion(); e.Handled = true;
    }
    private void StartClientMotion()
    {
        if (!motionSettings.AnimationsEnabled)
        {
            clientX = clientPlacement.X; clientY = clientPlacement.Y;
            clientConnection = ClientConnectionTarget();
            PaintClientHandle(); return;
        }
        if (clientAnimating) return;
        clientAnimating = true; clientFrame = Stopwatch.GetTimestamp(); CompositionTarget.Rendering += AnimateClientHandle;
    }
    private void AnimateClientHandle(object? sender, object e)
    {
        var now = Stopwatch.GetTimestamp(); var dt = Math.Clamp((now - clientFrame) / (double)Stopwatch.Frequency, 0, .05); clientFrame = now;
        var amount = 1 - Math.Exp(-24 * dt);
        clientX = GlassMotion.Lerp(clientX, clientPlacement.X, amount); clientY = GlassMotion.Lerp(clientY, clientPlacement.Y, amount);
        var target = ClientConnectionTarget();
        clientConnection = GlassMotion.Lerp(clientConnection, target, 1 - Math.Exp(-30 * dt));
        var settled = Math.Abs(clientX - clientPlacement.X) < .08 && Math.Abs(clientY - clientPlacement.Y) < .08 && Math.Abs(clientConnection - target) < .003;
        if (settled || !motionSettings.AnimationsEnabled)
        {
            clientX = clientPlacement.X; clientY = clientPlacement.Y; clientConnection = target;
            CompositionTarget.Rendering -= AnimateClientHandle; clientAnimating = false;
        }
        PaintClientHandle();
    }
    private double ClientConnectionTarget() => clientY >= ClientHandlePlacement.MinY && clientY <= ClientHandlePlacement.MaxY(Root.ActualHeight)
        && (clientPlacement.Pressed || clientPlacement.Dock != 0) ? ClientHandlePlacement.Connection(clientX, Root.ActualWidth).Amount : 0;
    private void PaintClientHandle()
    {
        if (!clientReady || compactAnimating || collapsed) return;
        var side = ClientHandlePlacement.Connection(clientX, Root.ActualWidth).Side;
        var edge = side < 0 ? ClientHandlePlacement.Left : ClientHandlePlacement.Right(Root.ActualWidth);
        clientOutline = LiquidHandleGeometry.Outline(clientX, clientY, edge, clientConnection);
        clientGlass.Data = OutlineGeometry(clientOutline);
        clientRim.Data = OutlineGeometry(clientOutline, clientConnection > .005 ? edge : null);
        panelRim.Data = PanelRimGeometry(Root.ActualWidth, Root.ActualHeight, side, clientY, clientConnection, settings.CornerRadius);
        panelRim.Stroke = OuterHighlight.BorderBrush; panelRim.Opacity = .2 + .8 * settings.EdgeHighlight / 100;
        var light = settings.Transparency <= 50;
        var fill = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        var alpha = (byte)Math.Clamp(255 * (1 - settings.Transparency / 100) + (clientHovered || clientPlacement.Pressed ? 28 : 12), 20, 245);
        fill.GradientStops.Add(new GradientStop { Color = Color.FromArgb(alpha, 255, 255, 255), Offset = 0 });
        fill.GradientStops.Add(new GradientStop { Color = Color.FromArgb((byte)Math.Max(15, (int)alpha - 30), 225, 236, 246), Offset = 1 });
        clientGlass.Fill = fill;
        clientRim.Stroke = new SolidColorBrush(light ? Color.FromArgb(125, 88, 111, 124) : Color.FromArgb((byte)(clientHovered ? 225 : 165), 239, 249, 255));
        clientHandle.Foreground = (Brush)Root.Resources["MainTextBrush"];
        Canvas.SetLeft(clientHandle, clientX - 18); Canvas.SetTop(clientHandle, clientY - 32);
        clientUsesPreview = HandlePreviewBounds.IsOutside(clientOutline, Root.ActualWidth, Root.ActualHeight);
        clientGlass.Visibility = clientRim.Visibility = clientUsesPreview ? Visibility.Collapsed : Visibility.Visible;
        clientHandle.Opacity = clientUsesPreview ? 0 : 1;
        if (clientUsesPreview)
        {
            clientPreview ??= new ClientDragPreview(hwnd);
            clientPreview.ShowOutline(clientOutline, clientX, clientY, clientConnection > .005 ? edge : null,
                AppWindow.Position, NativeWindow.GetDpiForWindow(hwnd) / 96d, clientGlass.Fill, clientRim.Stroke,
                clientHandle.Foreground, settings.Transparency, settings.Frosting);
        }
        else clientPreview?.HidePreview();
        SyncWindowGeometry();
    }
    internal static PathGeometry OutlineGeometry(IReadOnlyList<GlassPoint> points, double? openShore = null)
    {
        var geometry = new PathGeometry();
        var figure = new PathFigure { StartPoint = new Point(points[0].X, points[0].Y), IsClosed = openShore == null, IsFilled = openShore == null };
        geometry.Figures.Add(figure);
        for (var i = 1; i < points.Count; i++)
        {
            var p = points[i]; var previous = points[i - 1];
            if (openShore is { } shore && Math.Abs(p.X - shore) < .001 && Math.Abs(previous.X - shore) < .001 && Math.Abs(p.Y - previous.Y) > 1)
            {
                figure = new PathFigure { StartPoint = new Point(p.X, p.Y), IsFilled = false };
                geometry.Figures.Add(figure);
            }
            else figure.Segments.Add(new LineSegment { Point = new Point(p.X, p.Y) });
        }
        return geometry;
    }
    private static PathGeometry PanelRimGeometry(double width, double height, int side, double y, double connection, double cornerRadius)
    {
        // Inset the one-DIP stroke's centerline so its outer edge matches the native clip.
        var radius = Math.Max(0, cornerRadius - .5);
        var left = ClientHandlePlacement.Gutter + .5; var right = width - ClientHandlePlacement.Gutter - .5;
        var top = .5; var bottom = height - .5; var gap = connection > .005 ? 42 * connection : 0;
        var gapTop = Math.Clamp(y - gap, top + radius, bottom - radius);
        var gapBottom = Math.Clamp(y + gap, top + radius, bottom - radius);
        var geometry = new PathGeometry(); PathFigure figure = new() { StartPoint = new Point(left + radius, top), IsFilled = false }; geometry.Figures.Add(figure);
        void Line(double x, double yy) => figure.Segments.Add(new LineSegment { Point = new Point(x, yy) });
        void Arc(double x, double yy) => figure.Segments.Add(new ArcSegment { Point = new Point(x, yy), Size = new Size(radius, radius), SweepDirection = SweepDirection.Clockwise });
        void Break(double x, double yy) { figure = new PathFigure { StartPoint = new Point(x, yy), IsFilled = false }; geometry.Figures.Add(figure); }
        Line(right - radius, top); Arc(right, top + radius);
        if (side > 0 && gap > 0) { Line(right, gapTop); Break(right, gapBottom); }
        Line(right, bottom - radius); Arc(right - radius, bottom);
        Line(left + radius, bottom); Arc(left, bottom - radius);
        if (side < 0 && gap > 0) { Line(left, gapBottom); Break(left, gapTop); }
        Line(left, top + radius); Arc(left + radius, top);
        return geometry;
    }
    private void ShowClientMenu()
    {
        if (collapsed || compactAnimating || clientPlacement.Pressed) return;
        modelFlyout?.Hide(); Root.ContextFlyout.Hide(); clientFlyout?.Hide();
        var list = new StackPanel { Spacing = 3, Width = 168 };
        foreach (var provider in new[] { "Claude", "ChatGPT", "DeepSeek" })
        {
            var row = new Grid { ColumnSpacing = 18 };
            row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock { Text = provider, FontSize = 16 });
            if (provider == settings.Provider)
            {
                var check = new FontIcon { Glyph = "\uE73E", FontSize = 13, Foreground = (Brush)Root.Resources["AccentTextBrush"], VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(check, 1); row.Children.Add(check);
            }
            var button = new Button { Content = row, Tag = provider, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Style = (Style)Root.Resources["QuietButton"], Padding = new Thickness(12, 11, 12, 11), Background = new SolidColorBrush(provider == settings.Provider ? Color.FromArgb(28, 255, 255, 255) : Microsoft.UI.Colors.Transparent) };
            AutomationProperties.SetName(button, provider + (provider == settings.Provider ? "，已选择" : ""));
            button.Click += (s, e) => { clientFlyout?.Hide(); Provider_Click(s, e); };
            list.Children.Add(button);
        }
        var style = new Style(typeof(FlyoutPresenter));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Root.Resources["FloatingSurfaceBrush"]));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, Root.Resources["PanelStrokeBrush"]));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        style.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(16)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(7)));
        clientFlyout = new Flyout { Content = list, SystemBackdrop = new DesktopAcrylicBackdrop(), FlyoutPresenterStyle = style,
            Placement = clientX > Root.ActualWidth / 2 ? FlyoutPlacementMode.Right : FlyoutPlacementMode.Left, ShouldConstrainToRootBounds = false };
        clientFlyout.Opened += (_, _) => clientMenuOpen = true;
        clientFlyout.Closed += (_, _) => clientMenuOpen = false;
        clientFlyout.ShowAt(clientHandle);
    }
    private void StopClientHandle()
    {
        CompositionTarget.Rendering -= AnimateClientHandle; clientAnimating = false;
        clientFlyout?.Hide(); clientPlacement.End(Root.ActualWidth, Root.ActualHeight, cancel: true); clientHandle.ReleasePointerCaptures();
        clientPreview?.ClosePreview(); clientPreview = null;
    }
}
