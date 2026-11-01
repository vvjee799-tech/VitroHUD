using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
namespace VitroHUD.Desktop;

internal sealed class GlassBackdrop : SystemBackdrop
{
    private Windows.UI.Composition.Compositor? compositor;
    private Windows.UI.Composition.CompositionColorBrush? clear;
    private BackdropHost? host;
    private DesktopAcrylicController? fallback;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? startupTimer;
    private SystemBackdropConfiguration? config;
    private nint hwnd, dispatcherController;
    public bool CustomEffects { get; private set; }
    public string Mode { get; private set; } = "正在初始化玻璃";
    private double frost = 45, transparency = 70;
    private double contourInset = ClientHandlePlacement.Gutter, contourRadius = NativeWindow.CornerRadius;
    private IReadOnlyList<GlassPoint>? contourPoints;
    private bool contourIncludesBody = true;
    [StructLayout(LayoutKind.Sequential)] private struct QueueOptions { public int Size, ThreadType, ApartmentType; }
    [DllImport("CoreMessaging.dll")] private static extern int CreateDispatcherQueueController(QueueOptions options, out nint controller);
    public void Attach(nint owner) => hwnd = owner;
    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        try
        {
            xamlRoot.Content.DispatcherQueue.EnsureSystemDispatcherQueue();
            if (Windows.System.DispatcherQueue.GetForCurrentThread() == null) Marshal.ThrowExceptionForHR(CreateDispatcherQueueController(new() { Size = Marshal.SizeOf<QueueOptions>(), ThreadType = 2, ApartmentType = 2 }, out dispatcherController));
            NativeWindow.EnableAlpha(hwnd);
            compositor = new(); clear = compositor.CreateColorBrush(Color.FromArgb(0, 0, 0, 0)); connectedTarget.SystemBackdrop = clear;
            host = new BackdropHost(compositor, hwnd);
            SetContour(contourInset, contourRadius, contourPoints, contourIncludesBody);
            CustomEffects = true; Mode = "实时桌面玻璃 · 连续磨砂"; Apply(transparency, frost);
            // The XAML connection precedes ShowWindow. Sync after visibility commits.
            startupTimer = xamlRoot.Content.DispatcherQueue.CreateTimer(); startupTimer.Interval = TimeSpan.FromMilliseconds(50);
            var attempts = 0;
            startupTimer.Tick += (_, _) => { Sync(); if (++attempts >= 12) startupTimer.Stop(); }; startupTimer.Start();
        }
        catch (Exception ex)
        {
            CustomEffects = false;
            App.Log("Custom backdrop unavailable: " + ex); host?.Dispose(); host = null;
            connectedTarget.SystemBackdrop = null; clear?.Dispose(); clear = null;
            if (DesktopAcrylicController.IsSupported())
            {
                fallback = new(); config = new() { IsInputActive = true, Theme = SystemBackdropTheme.Light };
                fallback.SetSystemBackdropConfiguration(config); fallback.AddSystemBackdropTarget(connectedTarget);
                Mode = "系统玻璃 · 此设备使用固定磨砂"; Apply(transparency, frost);
            }
            else Mode = "系统暂不支持玻璃效果";
        }
        base.OnTargetConnected(connectedTarget, xamlRoot);
    }
    public void Sync() => host?.Sync();
    public void SetContour(double inset, double radius, IReadOnlyList<GlassPoint>? points, bool includeBody = true)
    {
        contourInset = inset; contourRadius = radius; contourPoints = points; contourIncludesBody = includeBody;
        if (host == null) return;
        host.ContourInset = inset; host.ContourRadius = radius; host.ContourPoints = points; host.ContourIncludesBody = includeBody;
    }
    public void Hide() => host?.Hide();
    public void Apply(double clarity, double frosting)
    {
        transparency = Math.Clamp(clarity, 0, 100); frost = Math.Clamp(frosting, 0, 100); host?.Apply(frost);
        // The white tint is drawn once by TintLayer; keep the system fallback neutral.
        if (fallback != null) { fallback.TintColor = Color.FromArgb(255, 255, 255, 255); fallback.TintOpacity = 0; fallback.LuminosityOpacity = 0; }
    }
    protected override void OnDefaultSystemBackdropConfigurationChanged(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        // Our sources have their own configurations and composition targets.
        // The default handler must not reconfigure this custom brush when XAML
        // switches foreground themes for the white tint.
        if (config != null) config.Theme = SystemBackdropTheme.Light;
    }
    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        CustomEffects = false;
        startupTimer?.Stop(); startupTimer = null; host?.Dispose(); host = null;
        if (fallback != null) { fallback.RemoveAllSystemBackdropTargets(); fallback.Dispose(); fallback = null; }
        disconnectedTarget.SystemBackdrop = null; clear?.Dispose(); compositor?.Dispose(); clear = null; compositor = null;
        base.OnTargetDisconnected(disconnectedTarget);
    }
}
