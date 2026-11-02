using VitroHUD;
using System.Runtime.InteropServices;

var checks = 0;
void Check(bool value, string label) { checks++; if (!value) throw new Exception(label); }
foreach (var name in new[] { "DeepSeek Harness", "DeepSeekHarness", "deepseek-harness", "dsh-desktop", "DSH Desktop", "DeepSeek Harness 0.0.1", "DeepSeek Harness 99.999.0-rc.88", "DeepSeek Harness 0.1.0 (x64)" })
    Check(ClientDiscovery.MatchesName("DeepSeek", name), "alias/version: " + name);
Check(!ClientDiscovery.MatchesName("DeepSeek", "DeepSeek Browser"), "different product");
Check(!ClientDiscovery.MatchesName("DeepSeek", "DeepSeek Harness Helper"), "helper is not a client");
Check(ClientDiscovery.MatchesIdentity("DeepSeek", "renamed-portable.exe", "DeepSeek Harness", "", ""), "renamed portable identified by product");
Check(!ClientDiscovery.MatchesIdentity("DeepSeek", "DeepSeekHarness.exe", "Unrelated app", "", ""), "filename alone is insufficient");
Check(!ClientDiscovery.MatchesIdentity("DeepSeek", "uninstall.exe", "DeepSeek Harness", "DeepSeek Harness", ""), "uninstaller rejected");
Check(!ClientDiscovery.MatchesIdentity("DeepSeek", "DeepSeekHarness-setup.exe", "DeepSeek Harness", "", ""), "installer rejected");
Check(!ClientDiscovery.MatchesIdentity("DeepSeek", "DeepSeek Harness.lnk", "DeepSeek Harness", "", ""), "shortcut not executed");
Check(ClientDiscovery.MatchesIdentity("ChatGPT", "ChatGPT.exe", "Codex", "Codex", "chrome.exe"), "Codex product identity");
Check(ClientDiscovery.MatchesIdentity("Claude", "portable.exe", "Claude", "", ""), "Claude custom root identity");
Check(ClientDiscovery.RegistryExecutable("\"D:\\自定义 软件\\DeepSeekHarness.exe\",0") == @"D:\自定义 软件\DeepSeekHarness.exe", "quoted display icon");
Check(ClientDiscovery.RegistryExecutable(@"D:\Custom Apps\DeepSeekHarness.exe --start") == @"D:\Custom Apps\DeepSeekHarness.exe", "unquoted app path with spaces");
Check(ClientDiscovery.RegistryExecutable("https://example.invalid/app.exe") == null, "remote URI rejected");
Check(ClientDiscovery.RegistryExecutable("DeepSeekHarness.exe") == null, "relative registry command rejected");
Check(ClientDiscovery.RegistryExecutable(@"\\server\share\DeepSeekHarness.exe") == null, "UNC executable rejected");
Check(ClientPageIdentity.Eligible("DeepSeek", "dsh-app://app/"), "current page origin");
Check(ClientPageIdentity.Eligible("DeepSeek", "file:///D:/custom/client/index.html"), "legacy file origin");
Check(ClientPageIdentity.Eligible("DeepSeek", "http://127.0.0.1:12345/chat/local-session"), "legacy loopback origin");
Check(ClientPageIdentity.Eligible("DeepSeek", "app://localhost/chat/test"), "legacy app origin");
foreach (var uri in new[] { "https://example.invalid/", "http://192.168.1.2:12345/", "devtools://devtools/", "file://server/share/index.html", "http://user:secret@127.0.0.1/" })
    Check(!ClientPageIdentity.Eligible("DeepSeek", uri), "external or privileged page rejected");
Check(ClientPageIdentity.SameTitle("DeepSeek", "同一会话", "同一会话 — DeepSeek Harness"), "version-independent title suffix");
Check(!ClientPageIdentity.SameTitle("DeepSeek", "会话一 — DeepSeek Harness", "会话二 — DeepSeek Harness"), "different conversation titles rejected");
Check(!ClientPageIdentity.SameTitle("DeepSeek", "", ""), "unnamed targets rejected");
Check(ClientPageIdentity.DebugEndpoint("ws://127.0.0.1:12345/devtools/page/test", 12345, "page"), "owned endpoint shape");
foreach (var uri in new[] { "ws://127.0.0.1:12346/devtools/page/test", "ws://127.0.0.2:12345/devtools/page/test", "ws://example.invalid:12345/devtools/page/test", "ws://127.0.0.1:12345/other", "ws://user:secret@127.0.0.1:12345/devtools/page/test" })
    Check(!ClientPageIdentity.DebugEndpoint(uri, 12345, "page"), "endpoint redirect rejected");
var root = Path.Combine(Path.GetTempPath(), "VitroHUD-discovery-" + Guid.NewGuid().ToString("N"));
try
{
    var installed = Path.Combine(root, "custom portable 路径", "app-0.0.1");
    Directory.CreateDirectory(installed);
    var exe = Path.Combine(installed, "DeepSeekHarness.exe");
    File.Copy(Path.Combine(AppContext.BaseDirectory, "fixture", "DeepSeekHarness.exe"), exe);
    Check(ClientDiscovery.IsExecutable("DeepSeek", exe), "old-version real PE in arbitrary directory");
    Check(!ClientDiscovery.IsExecutable("Claude", exe), "wrong provider real PE rejected");
    var portable = Path.Combine(root, "renamed-portable.exe"); File.Copy(exe, portable);
    Check(ClientDiscovery.IsExecutable("DeepSeek", portable), "renamed real PE metadata");
    Check(ClientDiscovery.DirectoryCandidates("DeepSeek", Path.GetDirectoryName(installed)!).Any(p => p == exe), "Squirrel version directory discovered");
    object? shell = null, shortcut = null;
    try
    {
        shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!);
        shortcut = ((dynamic)shell!).CreateShortcut(Path.Combine(root, "custom-client.lnk"));
        ((dynamic)shortcut).TargetPath = portable; ((dynamic)shortcut).Save();
    }
    finally
    {
        if (shortcut != null) Marshal.FinalReleaseComObject(shortcut);
        if (shell != null) Marshal.FinalReleaseComObject(shell);
    }
    Check(ClientDiscovery.ShortcutCandidates("DeepSeek", [root]).Contains(portable), "custom shortcut resolves renamed portable without a user-selected path");
    File.Delete(exe);
    Check(!ClientDiscovery.IsExecutable("DeepSeek", exe), "stale installation path rejected");
}
finally { Directory.Delete(root, true); }
Console.WriteLine($"Client discovery and endpoint checks passed: {checks}");
