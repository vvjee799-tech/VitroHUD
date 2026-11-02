using System.IO;
using System.Security.AccessControl;
using System.Text.Json;

namespace VitroHUD;

internal static class AppPaths
{
    // Keep the original instance/connection locks during the rename so that
    // launching an older installation cannot create a second active sidebar.
    internal const string InstancePrefix = @"Local\AgentGlass.";
    internal const string ClaudeLockPrefix = @"Local\AgentGlass.Claude.";
    private static readonly string Root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static readonly string[] MigratedFiles = ["settings.json", "chatgpt-connection.json", "claude-connection.json"];

    internal static string FilePath(string name)
    {
        if (!MigratedFiles.Contains(name) && name != "client-paths.json") throw new ArgumentException("Unknown application data file", nameof(name));
        Migrate(Root);
        return Path.Combine(Root, "VitroHUD", name);
    }

    internal static void Migrate(string root)
    {
        var current = Path.Combine(root, "VitroHUD");
        var legacy = Path.Combine(root, "AgentGlass");
        var marker = Path.Combine(current, ".agentglass-migrated-v1");
        if (File.Exists(marker)) return;
        using var gate = new Mutex(false, @"Local\VitroHUD.Migration." + Wire.Hash(Path.GetFullPath(root)));
        var locked = false;
        try
        {
            try { locked = gate.WaitOne(2500); } catch (AbandonedMutexException) { locked = true; }
            if (!locked) throw new IOException("Application data migration is busy");
            Directory.CreateDirectory(current);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Application data directory is a link");
            if (File.Exists(marker)) return;
            foreach (var name in MigratedFiles)
            {
                var source = new FileInfo(Path.Combine(legacy, name));
                var target = new FileInfo(Path.Combine(current, name));
                if (target.Exists || !source.Exists || source.Length > 16000 || (source.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                try
                {
                    using var parsed = JsonDocument.Parse(File.ReadAllText(source.FullName));
                    if (parsed.RootElement.ValueKind != JsonValueKind.Object) continue;
                }
                catch (JsonException) { continue; }
                File.Copy(source.FullName, target.FullName, false);
                target.SetAccessControl(source.GetAccessControl(AccessControlSections.Access));
            }
            // Only migrate once. Deleting a new connection intentionally must
            // never bring its old binding back on the next request.
            File.WriteAllText(marker, "1");
        }
        finally { if (locked) gate.ReleaseMutex(); }
    }
}
