using System.Diagnostics;
using Windows.Management.Deployment;

namespace VitroHUD.Desktop;

internal static class BackgroundLauncher
{
    public static string? FindExecutable(string provider)
    {
        var name = provider switch { "Claude" => "claude", "DeepSeek" => "DeepSeek Harness", _ => "ChatGPT" };
        foreach (var process in Process.GetProcessesByName(name))
        {
            try { if (process.MainWindowHandle != 0 && process.MainModule?.FileName is { } path) return path; } catch { }
            finally { process.Dispose(); }
        }
        if (provider == "DeepSeek")
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "DeepSeek Harness", "DeepSeek Harness.exe");
            return File.Exists(path) ? path : null;
        }
        try
        {
            var packages = new PackageManager().FindPackagesForUser("");
            var package = packages.Where(p => provider == "Claude" ? p.Id.Name == "Claude" : p.Id.Name is "OpenAI.Codex" or "OpenAI.ChatGPT-Desktop").OrderByDescending(p => p.Id.Version.Major).ThenByDescending(p => p.Id.Version.Build).FirstOrDefault();
            if (package != null)
            {
                var path = Path.Combine(package.InstalledLocation.Path, "app", provider == "Claude" ? "Claude.exe" : "ChatGPT.exe");
                if (File.Exists(path)) return path;
            }
        }
        catch { }
        return null;
    }
    public static bool IsRunning(string path)
    {
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(path)))
        {
            try { if (string.Equals(process.MainModule?.FileName, path, StringComparison.OrdinalIgnoreCase)) return true; } catch { }
            finally { process.Dispose(); }
        }
        return false;
    }
    public static void Start(string path)
    {
        ClientActivation.Start(path);
    }
}
