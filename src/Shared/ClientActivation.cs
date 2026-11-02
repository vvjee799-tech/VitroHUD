using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VitroHUD;

internal static class ClientActivation
{
    internal const string BackgroundArguments = "--disable-background-timer-throttling --disable-renderer-backgrounding --disable-backgrounding-occluded-windows --force-renderer-accessibility";
    internal const string Arguments = "--remote-debugging-address=127.0.0.1 --remote-debugging-port=0 " + BackgroundArguments;
    [ComImport, Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivationManager
    {
        [PreserveSig] int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appId, [MarshalAs(UnmanagedType.LPWStr)] string arguments, uint options, out uint processId);
    }
    public static uint Start(string path)
    {
        var provider = new[] { "DeepSeek", "Claude", "ChatGPT" }.FirstOrDefault(p => ClientDiscovery.IsExecutable(p, path));
        if (provider == null) throw new InvalidOperationException("程序未识别为支持的客户端主程序。");
        DiagnosticLog.Write(LogEvent.ClientLaunchRequested, provider, "launch", clientPath: path);
        if (provider == "Claude")
            throw new NotSupportedException("当前 Claude 的后台写入尚未通过验证，请使用前台模式。");
        foreach (var running in Process.GetProcessesByName(System.IO.Path.GetFileNameWithoutExtension(path)))
        {
            using (running)
                if (string.Equals(running.MainModule?.FileName, path, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("请先从原客户端菜单退出，再连接后台会话。");
        }
        if (path.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase))
        {
            var package = new System.IO.DirectoryInfo(System.IO.Path.GetDirectoryName(path)!).Parent!.Name;
            var name = package.Split('_')[0]; var publisher = package[(package.LastIndexOf("__", StringComparison.Ordinal) + 2)..];
            var appId = name + "_" + publisher + (name == "Claude" ? "!Claude" : "!App");
            var manager = (IActivationManager)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C"))!)!;
            try { Marshal.ThrowExceptionForHR(manager.ActivateApplication(appId, Arguments, 0, out var processId)); DiagnosticLog.Write(LogEvent.ClientLaunched, provider, "launch", clientPath: path); return processId; }
            finally { Marshal.ReleaseComObject(manager); }
        }
        // The original client must outlive this launcher. Inheriting a probe's
        // short-lived stdout/stderr pipes can crash Electron later with EPIPE.
        using var process = Process.Start(new ProcessStartInfo(path, Arguments) { UseShellExecute = true });
        DiagnosticLog.Write(LogEvent.ClientLaunched, provider, "launch", clientPath: path);
        return (uint)(process?.Id ?? 0);
    }
}
