using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

internal static class WindowGeometry
{
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    private delegate bool EnumChild(nint window, nint data);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint h, out Rect r);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint h, out Rect r);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint h, ref Point p);
    [DllImport("user32.dll")] private static extern nint GetWindowLongPtr(nint h, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint h, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(nint h, EnumChild callback, nint data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumChild callback, nint data);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint h, out uint processId);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint h);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint h);
    [DllImport("user32.dll")] private static extern int GetWindowRgn(nint h, nint region);
    [DllImport("gdi32.dll")] private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern int GetRgnBox(nint region, out Rect bounds);
    [DllImport("gdi32.dll")] private static extern bool PtInRegion(nint region, int x, int y);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint region);
    public static object Read()
    {
        var process = Process.GetProcessesByName("VitroHUD").Single();
        var window = process.MainWindowHandle;
        object Region(nint h, int width, int height)
        {
            var region = CreateRectRgn(0, 0, 0, 0);
            try
            {
                var type = GetWindowRgn(h, region); GetRgnBox(region, out var box);
                var corners = new[] { (0, 0), (width - 1, 0), (0, height - 1), (width - 1, height - 1) };
                var edges = new[] { (width / 2, 0), (width - 1, height / 2), (width / 2, height - 1), (0, height / 2) };
                return new { type, bounds = new[] { box.Left, box.Top, box.Right, box.Bottom }, cornersInside = corners.Select(p => PtInRegion(region, p.Item1, p.Item2)).ToArray(), edgeCentersInside = edges.Select(p => PtInRegion(region, p.Item1, p.Item2)).ToArray() };
            }
            finally { DeleteObject(region); }
        }
        object Describe(nint h)
        {
            GetWindowRect(h, out var outer); GetClientRect(h, out var client); var origin = new Point(); ClientToScreen(h, ref origin);
            var name = new StringBuilder(256); GetClassName(h, name, 256);
            return new { handle = h.ToInt64(), @class = name.ToString(), visible = IsWindowVisible(h), dpi = GetDpiForWindow(h), style = GetWindowLongPtr(h, -16).ToInt64().ToString("X"), outer = new[] { outer.Left, outer.Top, outer.Right, outer.Bottom }, client = new[] { origin.X, origin.Y, client.Right, client.Bottom }, region = Region(h, outer.Right - outer.Left, outer.Bottom - outer.Top) };
        }
        var children = new List<object>(); EnumChildWindows(window, (h, _) => { children.Add(Describe(h)); return true; }, 0);
        var backdrops = new List<object>();
        EnumWindows((h, _) => { GetWindowThreadProcessId(h, out var processId); var name = new StringBuilder(256); GetClassName(h, name, 256); if (processId == process.Id && name.ToString().StartsWith("VitroHUD.Backdrop.")) backdrops.Add(Describe(h)); return true; }, 0);
        return new { main = Describe(window), backdrops, children };
    }
}
