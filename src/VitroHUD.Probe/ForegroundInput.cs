using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;

namespace VitroHUD;

internal static class ForegroundInput
{
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public Keyboard Keyboard; [FieldOffset(0)] public Mouse Mouse; }
    [StructLayout(LayoutKind.Sequential)] private struct Keyboard { public ushort Vk; public ushort Scan; public uint Flags; public uint Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct Mouse { public int X; public int Y; public uint Data; public uint Flags; public uint Time; public nuint Extra; }
    private static Input Key(ushort key, bool up = false) => new() { Type = 1, Data = new() { Keyboard = new() { Vk = key, Flags = up ? 2u : 0 } } };

    public static bool Paste(AutomationElement editor, nint target, long originWindow, string text, out string error)
    {
        error = "";
        var previous = GetForegroundWindow();
        if (originWindow == 0 || previous != (nint)originWindow) { error = "请从侧栏点击发送后再试"; return false; }
        try
        {
            if (!SetForegroundWindow(target)) { error = "无法激活原客户端，请先打开其窗口"; return false; }
            editor.SetFocus();
            if (GetForegroundWindow() != target || !editor.Current.HasKeyboardFocus) { error = "未能确认原客户端输入焦点，已取消"; return false; }
            var success = false; var clipboardError = "";
            var clipboard = new Thread(() => success = PasteClipboard(target, text, out clipboardError));
            clipboard.SetApartmentState(ApartmentState.STA);
            clipboard.Start(); clipboard.Join();
            error = clipboardError; return success;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ElementNotAvailableException)
        {
            error = "原客户端输入未完成，请在那里检查草稿"; return false;
        }
        finally
        {
            // Do not steal focus back if the user moved to a different window.
            if (GetForegroundWindow() == target) SetForegroundWindow(previous);
        }
    }
    private static bool PasteClipboard(nint target, string text, out string error)
    {
        error = ""; System.Windows.IDataObject? oldClipboard = null; uint ownSequence = 0;
        try
        {
            oldClipboard = Clipboard.GetDataObject();
            Clipboard.SetText(text); ownSequence = GetClipboardSequenceNumber();
            if (GetForegroundWindow() != target) { error = "输入焦点已变化，已取消粘贴"; return false; }
            var input = new[] { Key(0x11), Key(0x56), Key(0x56, true), Key(0x11, true) };
            if (SendInput((uint)input.Length, input, Marshal.SizeOf<Input>()) != input.Length) { error = "原客户端未接受输入，未提交消息"; return false; }
            Thread.Sleep(160); return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        { error = "原客户端粘贴未完成，请检查草稿"; return false; }
        finally
        {
            try { if (ownSequence != 0 && GetClipboardSequenceNumber() == ownSequence) { if (oldClipboard != null) Clipboard.SetDataObject(oldClipboard, true); else Clipboard.Clear(); } } catch { }
        }
    }
}
