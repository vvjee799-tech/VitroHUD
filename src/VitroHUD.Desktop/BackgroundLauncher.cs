using Windows.Management.Deployment;

namespace VitroHUD.Desktop;

internal static class BackgroundLauncher
{
    internal static void DiscoverInstalledClients()
    {
        foreach (var provider in new[] { "DeepSeek", "Claude", "ChatGPT" })
        {
            try { FindExecutable(provider); } catch { }
        }
    }
    public static string? FindExecutable(string provider)
    {
        var discovered = ClientDiscovery.FindExecutable(provider);
        if (discovered != null) { ClientDiscovery.TryRemember(provider, discovered); return discovered; }
        try
        {
            var packages = new PackageManager().FindPackagesForUser("");
            var package = packages.Where(p => provider == "Claude" ? p.Id.Name == "Claude" : p.Id.Name is "OpenAI.Codex" or "OpenAI.ChatGPT-Desktop").OrderByDescending(p => p.Id.Version.Major).ThenByDescending(p => p.Id.Version.Build).FirstOrDefault();
            if (package != null)
            {
                var path = Path.Combine(package.InstalledLocation.Path, "app", provider == "Claude" ? "Claude.exe" : "ChatGPT.exe");
                if (ClientDiscovery.IsExecutable(provider, path)) { ClientDiscovery.TryRemember(provider, path); return path; }
            }
        }
        catch { }
        return null;
    }
    public static bool IsRunning(string path) => new[] { "DeepSeek", "Claude", "ChatGPT" }.Any(p => ClientDiscovery.IsRunning(p, path));
    public static void Start(string path)
    {
        ClientActivation.Start(path);
    }
}
