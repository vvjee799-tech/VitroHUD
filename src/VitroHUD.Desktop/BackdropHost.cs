using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas.Effects;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using Microsoft.UI.Composition.SystemBackdrops;
using WinRT;
namespace VitroHUD.Desktop;

// Host-backdrop rendering needs its own non-interactive HWND below the editor.
internal sealed class BackdropHost : IDisposable
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WindowClass
    {
        public uint Size, Style; public nint Procedure; public int ClassExtra, WindowExtra; public nint Instance, Icon, Cursor, Background;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Menu;
        [MarshalAs(UnmanagedType.LPWStr)] public string ClassName;
        public nint SmallIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Accent { public int State, Flags; public uint Color; public int Animation; }
    [StructLayout(LayoutKind.Sequential)] private struct AttributeData { public int Attribute; public nint Data; public nuint Size; }
    private delegate nint WindowProc(nint h, uint m, nint w, nint l);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassEx(ref WindowClass value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint DefWindowProc(nint h, uint m, nint w, nint l);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint h, out Rect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint h, ref Point point);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint h);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint h);
    [DllImport("user32.dll")] private static extern int SetWindowCompositionAttribute(nint h, ref AttributeData data);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetLayeredWindowAttributes(nint h, uint colorKey, byte alpha, uint flags);
    [ComImport, Guid("29E691FA-4567-4DCA-B319-D0F207EB6807"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICompositorDesktopInterop
    {
        [PreserveSig] int CreateDesktopWindowTarget(nint hwnd, [MarshalAs(UnmanagedType.Bool)] bool topmost, out nint target);
        [PreserveSig] int EnsureOnThread(uint threadId);
    }
    [ComImport, Guid("397DAFE4-B6C2-5BB9-951D-F5707DE8B7BC"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISystemBackdropTarget
    {
        [PreserveSig] int GetIids(out uint count, out nint iids);
        [PreserveSig] int GetRuntimeClassName(out nint name);
        [PreserveSig] int GetTrustLevel(out int level);
        [PreserveSig] int GetBackdrop(out nint brush);
        [PreserveSig] int SetBackdrop(nint brush);
    }
    private readonly nint owner;
    private readonly NativeWindow.RoundedRegion roundedRegion = new();
    private readonly Compositor compositor;
    private nint hwnd;
    private readonly WindowProc procedure = DefWindowProc;
    private DesktopWindowTarget? target;
    private SpriteVisual? visual;
    private CompositionEffectBrush? brush;
    private CompositionEffectFactory? effectFactory;
    private CompositionBrush? material;
    private ISystemBackdropTarget? systemTarget;
    private CompositionRoundedRectangleGeometry? geometry;
    private ShapeVisual? maskVisual;
    private CompositionVisualSurface? maskSurface;
    private CompositionSurfaceBrush? maskBrush;
    private DesktopAcrylicController? desktopSource;
    private SystemBackdropConfiguration? configuration;
    private double frosting = 45;
    private int appliedAlpha = -1;
    internal double ContourInset { get; set; } = ClientHandlePlacement.Gutter;
    internal double ContourRadius { get; set; } = NativeWindow.CornerRadius;
    internal IReadOnlyList<GlassPoint>? ContourPoints { get; set; }
    internal bool ContourIncludesBody { get; set; } = true;
    public BackdropHost(Compositor compositor, nint owner)
    {
        this.owner = owner;
        this.compositor = compositor;
        var instance = GetModuleHandle(null);
        var className = "VitroHUD.Backdrop." + Environment.ProcessId;
        var wc = new WindowClass { Size = (uint)Marshal.SizeOf<WindowClass>(), Procedure = Marshal.GetFunctionPointerForDelegate(procedure), Instance = instance, ClassName = className };
        if (RegisterClassEx(ref wc) == 0) throw new InvalidOperationException("无法注册背景合成窗口");
        hwnd = CreateWindowEx(0x080800A0, className, "", 0x80000000, 0, 0, 10, 10, 0, 0, instance, 0);
        if (hwnd == 0) throw new InvalidOperationException("无法建立背景合成窗口");
        try
        {
            ApplyHostOpacity();
            var flag = 1; NativeWindow.DwmSetWindowAttribute(hwnd, 17, ref flag, sizeof(int));
            var corner = 1; NativeWindow.DwmSetWindowAttribute(hwnd, 33, ref corner, sizeof(int));
            var noBorder = -2; NativeWindow.DwmSetWindowAttribute(hwnd, 34, ref noBorder, sizeof(int));
            var memory = Marshal.AllocHGlobal(Marshal.SizeOf<Accent>());
            try
            {
                Marshal.StructureToPtr(new Accent { State = 5 }, memory, false);
                var data = new AttributeData { Attribute = 19, Data = memory, Size = (nuint)Marshal.SizeOf<Accent>() };
                if (SetWindowCompositionAttribute(hwnd, ref data) == 0) throw new InvalidOperationException("Host backdrop 不可用");
            }
            finally { Marshal.FreeHGlobal(memory); }
            var interop = compositor.As<ICompositorDesktopInterop>();
            Marshal.ThrowExceptionForHR(interop.CreateDesktopWindowTarget(hwnd, true, out var pointer));
            try { target = MarshalInterface<DesktopWindowTarget>.FromAbi(pointer); }
            finally { Marshal.Release(pointer); }
            visual = compositor.CreateSpriteVisual(); target.Root = visual;
            geometry = compositor.CreateRoundedRectangleGeometry();
            maskVisual = compositor.CreateShapeVisual();
            var maskShape = compositor.CreateSpriteShape(geometry);
            maskShape.FillBrush = compositor.CreateColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255));
            maskVisual.Shapes.Add(maskShape);
            maskSurface = compositor.CreateVisualSurface(); maskSurface.SourceVisual = maskVisual;
            maskBrush = compositor.CreateSurfaceBrush(maskSurface); maskBrush.Stretch = CompositionStretch.None;
            systemTarget = target.As<ISystemBackdropTarget>();
            desktopSource = new() { Kind = DesktopAcrylicKind.Thin, TintColor = Windows.UI.Color.FromArgb(255, 255, 255, 255), TintOpacity = 0, LuminosityOpacity = 0 };
            configuration = new() { IsInputActive = true, Theme = SystemBackdropTheme.Light };
            desktopSource.SetSystemBackdropConfiguration(configuration);
            if (!desktopSource.SetTarget(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd), target)) throw new InvalidOperationException("无法连接桌面背景源");
            Sync();
        }
        catch { Dispose(); throw; }
    }
    public void Sync()
    {
        if (hwnd == 0 || visual == null || geometry == null) return;
        if (systemTarget != null && brush == null)
        {
            Marshal.ThrowExceptionForHR(systemTarget.GetBackdrop(out var materialPointer));
            if (materialPointer != 0)
            {
                try { material?.Dispose(); material = MarshalInterface<CompositionBrush>.FromAbi(materialPointer); }
                finally { Marshal.Release(materialPointer); }
                var acrylic = material as CompositionEffectBrush ?? throw new InvalidOperationException("系统未提供可组合的磨砂源");
                var source = acrylic.GetSourceParameter("HostBackdrop");
                var (amount, _) = FrostParameters();
                var blur = new GaussianBlurEffect { Name = "Frost", BlurAmount = amount, BorderMode = EffectBorderMode.Hard, Source = new CompositionEffectSourceParameter("Desktop") };
                // SystemBackdrop is a separate visual behind Root. Clip the actual
                // backdrop brush; clipping Root alone leaves rectangular corners.
                var rounded = new AlphaMaskEffect { Source = blur, AlphaMask = new CompositionEffectSourceParameter("Mask") };
                effectFactory = compositor.CreateEffectFactory(rounded, ["Frost.BlurAmount"]);
                brush = effectFactory.CreateBrush();
                brush.SetSourceParameter("Desktop", source);
                brush.SetSourceParameter("Mask", maskBrush);
            }
        }
        InstallBrush();
        ApplyHostOpacity();
        if (frosting <= 0 || !IsWindowVisible(owner) || IsIconic(owner)) { NativeWindow.ShowWindow(hwnd, 0); return; }
        if (!GetClientRect(owner, out var rect)) return;
        var origin = new Point();
        if (!ClientToScreen(owner, ref origin)) return;
        var width = rect.Right - rect.Left; var height = rect.Bottom - rect.Top;
        visual.Size = new Vector2(width, height); geometry.Size = visual.Size;
        maskVisual!.Size = visual.Size; maskSurface!.SourceSize = visual.Size;
        var dpi = NativeWindow.GetDpiForWindow(owner);
        geometry.CornerRadius = new Vector2((float)(ContourRadius * dpi / 96d));
        // The native resize margin is outside XAML content. Painting it creates a
        // second glass frame around the sidebar, especially at non-100% DPI.
        roundedRegion.Update(hwnd, width, height, dpi, ContourInset, ContourRadius, ContourPoints, ContourIncludesBody);
        NativeWindow.SetWindowPos(hwnd, owner, origin.X, origin.Y, width, height, 0x50);
        _ = compositor.RequestCommitAsync();
    }
    public void Hide() { if (hwnd != 0) NativeWindow.ShowWindow(hwnd, 0); }
    private void InstallBrush()
    {
        if (brush == null || systemTarget == null) return;
        var pointer = MarshalInspectable<CompositionBrush>.FromManaged(brush);
        try { Marshal.ThrowExceptionForHR(systemTarget.SetBackdrop(pointer)); }
        finally { Marshal.Release(pointer); }
    }
    public void Apply(double value)
    {
        frosting = Math.Clamp(value, 0, 100);
        // A desktop source must remain in the system-backdrop slot. Drawing it in
        // an ordinary visual loses the desktop sampling context.
        var (amount, _) = FrostParameters();
        brush?.Properties.InsertScalar("Frost.BlurAmount", amount);
        InstallBrush();
        Sync(); _ = compositor.RequestCommitAsync();
    }
    private (float Amount, float Mix) FrostParameters()
    {
        var lowRange = Math.Clamp(frosting / 12, 0, 1);
        var mix = lowRange * lowRange * (3 - 2 * lowRange);
        return ((float)(frosting * 2 * mix), (float)mix);
    }
    private void ApplyHostOpacity()
    {
        // Fade the complete non-interactive backdrop HWND, including its native
        // host-backdrop base. Alpha on the effect brush alone produces a gray base.
        var (_, mix) = FrostParameters();
        var alpha = (int)Math.Round(mix * 255);
        if (alpha == appliedAlpha) return;
        if (!SetLayeredWindowAttributes(hwnd, 0, (byte)alpha, 2))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "无法调整磨砂背景强度");
        appliedAlpha = alpha;
    }
    public void Dispose()
    {
        desktopSource?.Dispose(); desktopSource = null;
        if (target != null) target.Root = null;
        visual?.Dispose(); brush?.Dispose(); effectFactory?.Dispose(); material?.Dispose(); maskBrush?.Dispose(); maskSurface?.Dispose(); maskVisual?.Dispose(); geometry?.Dispose(); target?.Dispose(); visual = null; brush = null; effectFactory = null; material = null; geometry = null; target = null;
        if (hwnd != 0) { DestroyWindow(hwnd); hwnd = 0; }
    }
}
