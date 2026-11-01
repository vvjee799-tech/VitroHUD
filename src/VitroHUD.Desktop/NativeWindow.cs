using System.Runtime.InteropServices;
namespace VitroHUD.Desktop;
internal static class NativeWindow
{
    [StructLayout(LayoutKind.Sequential)] private struct Margins { public int Left, Right, Top, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct BlurBehind { public uint Flags; [MarshalAs(UnmanagedType.Bool)] public bool Enable; public nint Region; [MarshalAs(UnmanagedType.Bool)] public bool Transition; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Accent { public int State, Flags; public uint Color; public int Animation; }
    [StructLayout(LayoutKind.Sequential)] private struct AttributeData { public int Attribute; public nint Data; public nuint Size; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MinMaxInfo { public Point Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
    [StructLayout(LayoutKind.Sequential)] private struct StyleChange { public uint Old, New; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    internal static bool TryCursorPoint(out Windows.Graphics.PointInt32 screen)
    {
        var result = GetCursorPos(out var point);
        screen = new(point.X, point.Y);
        return result;
    }
    [DllImport("user32.dll")] private static extern int SetWindowCompositionAttribute(nint h, ref AttributeData value);
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(nint h, ref Margins margins);
    [DllImport("dwmapi.dll")] private static extern int DwmEnableBlurBehindWindow(nint h, ref BlurBehind blur);
    [DllImport("gdi32.dll")] private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateRoundRectRgn(int left, int top, int right, int bottom, int ellipseWidth, int ellipseHeight);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreatePolygonRgn([In] Point[] points, int count, int fillMode);
    [DllImport("gdi32.dll")] private static extern int CombineRgn(nint destination, nint first, nint second, int mode);
    [DllImport("user32.dll", SetLastError = true)] private static extern int SetWindowRgn(nint window, nint region, [MarshalAs(UnmanagedType.Bool)] bool redraw);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")] private static extern nint GetStockObject(int index);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint h, out Rect rect);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint h, out Rect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint h, ref Point point);
    [DllImport("user32.dll")] private static extern int FillRect(nint dc, ref Rect rect, nint brush);
    [DllImport("user32.dll")] private static extern nint GetDC(nint h);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint h, nint dc);
    [DllImport("dwmapi.dll")] internal static extern int DwmSetWindowAttribute(nint h, int attr, ref int value, int size);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(nint h);
    [DllImport("user32.dll")] internal static extern nint GetWindowLongPtr(nint h, int index);
    [DllImport("user32.dll")] internal static extern nint SetWindowLongPtr(nint h, int index, nint value);
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(nint h, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] internal static extern bool RegisterHotKey(nint h, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(nint h, int id);
    [DllImport("comctl32.dll")] internal static extern bool SetWindowSubclass(nint h, SubclassProc callback, nuint id, nuint data);
    [DllImport("comctl32.dll")] internal static extern nint DefSubclassProc(nint h, uint message, nint w, nint l);
    [DllImport("comctl32.dll")] internal static extern bool RemoveWindowSubclass(nint h, SubclassProc callback, nuint id);
    [DllImport("user32.dll")] internal static extern bool ShowWindow(nint h, int command);
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(nint h);
    [DllImport("user32.dll")] internal static extern bool AllowSetForegroundWindow(uint pid);
    [DllImport("user32.dll")] internal static extern bool IsIconic(nint h);
    internal delegate nint SubclassProc(nint h, uint message, nint w, nint l, nuint id, nuint data);
    internal const double CornerRadius = AppearanceSettings.DefaultCornerRadius;
    internal static bool TryScreenPoint(nint window, int clientX, int clientY, out Windows.Graphics.PointInt32 screen)
    {
        var point = new Point { X = clientX, Y = clientY };
        var result = ClientToScreen(window, ref point);
        screen = new(point.X, point.Y);
        return result;
    }
    internal sealed class RoundedRegion
    {
        private (int Width, int Height, int Diameter, int Inset, int OutlineHash, bool IncludeBody) applied;
        internal void Update(nint window)
        {
            if (!GetWindowRect(window, out var bounds)) return;
            Update(window, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top, GetDpiForWindow(window));
        }
        internal void Update(nint window, int width, int height, uint dpi, double inset = 0, double radius = CornerRadius, IReadOnlyList<GlassPoint>? outline = null, bool includeBody = true)
        {
            if (width <= 0 || height <= 0) return;
            var scale = dpi / 96d;
            var diameter = (int)Math.Round(radius * 2 * scale);
            var side = (int)Math.Round(inset * scale);
            Point[]? polygon = null; var hash = new HashCode();
            if (outline is { Count: > 2 })
            {
                polygon = outline.Select(p => new Point { X = (int)Math.Round(p.X * scale), Y = (int)Math.Round(p.Y * scale) }).ToArray();
                foreach (var p in polygon) { hash.Add(p.X); hash.Add(p.Y); }
            }
            var next = (width, height, diameter, side, polygon == null ? 0 : hash.ToHashCode(), includeBody);
            if (applied == next) return;
            var region = includeBody ? CreateRoundRectRgn(side, 0, width - side + 1, height + 1, diameter, diameter) : CreateRectRgn(0, 0, 0, 0);
            if (region == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "无法建立玻璃窗口圆角区域");
            if (polygon != null)
            {
                var handle = CreatePolygonRgn(polygon, polygon.Length, 1);
                if (handle == 0) { DeleteObject(region); throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "无法建立侧边入口轮廓"); }
                var combined = CombineRgn(region, region, handle, 2); DeleteObject(handle);
                if (combined == 0) { DeleteObject(region); throw new InvalidOperationException("无法合并玻璃轮廓"); }
            }
            // Clip every native surface, including the host-backdrop base beneath
            // the masked brush. A visual mask does not clip that native base.
            var previous = applied; applied = next; // SetWindowRgn sends window-position messages synchronously.
            if (SetWindowRgn(window, region, true) == 0)
            {
                applied = previous; DeleteObject(region);
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "无法裁剪玻璃窗口边界");
            }
            // Windows owns region after success; deleting it here is invalid.
        }
    }
    internal static void RemoveNonClientFrame(nint h)
    {
        // WinUI's resizable presenter retains WS_THICKFRAME even with its border
        // hidden. Remove that painted frame; resize hit testing is provided below.
        SetWindowLongPtr(h, -16, (nint)(GetWindowLongPtr(h, -16).ToInt64() & ~0x00C40000L));
        SetWindowPos(h, 0, 0, 0, 0, 0, 0x37);
    }
    internal static void SuppressFrameStyle(nint pointer)
    {
        var change = Marshal.PtrToStructure<StyleChange>(pointer); change.New &= ~0x00C40000u;
        Marshal.StructureToPtr(change, pointer, false);
    }
    internal static void SetMinimumSize(nint h, nint pointer, int width, int height)
    {
        var info = Marshal.PtrToStructure<MinMaxInfo>(pointer); var scale = GetDpiForWindow(h) / 96d;
        info.MinTrackSize = new Point { X = (int)(width * scale), Y = (int)(height * scale) };
        Marshal.StructureToPtr(info, pointer, false);
    }
    internal static void EnableAlpha(nint h)
    {
        var margins = new Margins(); DwmExtendFrameIntoClientArea(h, ref margins);
        var region = CreateRectRgn(-2, -2, -1, -1);
        try { var blur = new BlurBehind { Flags = 3, Enable = true, Region = region }; Marshal.ThrowExceptionForHR(DwmEnableBlurBehindWindow(h, ref blur)); }
        finally { DeleteObject(region); }
        var dc = GetDC(h); try { ClearBackground(h, dc); } finally { ReleaseDC(h, dc); }
    }
    internal static bool ClearBackground(nint h, nint dc) => GetClientRect(h, out var rect) && FillRect(dc, ref rect, GetStockObject(4)) != 0;
    internal static void EnableHost(nint h)
    {
        var memory = Marshal.AllocHGlobal(Marshal.SizeOf<Accent>());
        try
        {
            Marshal.StructureToPtr(new Accent { State = 5 }, memory, false);
            var data = new AttributeData { Attribute = 19, Data = memory, Size = (nuint)Marshal.SizeOf<Accent>() };
            if (SetWindowCompositionAttribute(h, ref data) == 0) throw new InvalidOperationException("Host backdrop 不可用");
        }
        finally { Marshal.FreeHGlobal(memory); }
    }
    internal static void Topmost(nint h, bool top) => SetWindowPos(h, top ? -1 : -2, 0, 0, 0, 0, 0x13);
    internal static void PassThrough(nint h, bool enabled)
    {
        var value = GetWindowLongPtr(h, -20).ToInt64();
        SetWindowLongPtr(h, -20, (nint)(enabled ? value | 0x08000020L : value & ~0x08000020L));
    }
}
