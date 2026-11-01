using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;
using VitroHUD;

namespace VitroHUD.Probe;

// Private, temporary files carry requests to the original Claude renderer.
// The renderer uses its own session APIs; no network listener or model API.
internal sealed partial class ClaudeConnection
{
    internal sealed record Configuration(string SessionId, string Workspace, string Relative, string Nonce, int ProcessId, long ProcessStart, long Window);
    internal static readonly string ConnectionPath = AppPaths.FilePath("claude-connection.json");
    private readonly Configuration configuration;
    private readonly string folder;
    internal string SourceKey => Wire.Hash($"Claude|files|{configuration.ProcessId}|{configuration.ProcessStart}|{configuration.Window}|{configuration.SessionId}|{configuration.Nonce}");
    [GeneratedRegex(@"^local_[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", RegexOptions.IgnoreCase)] private static partial Regex Session();
    [GeneratedRegex(@"^\.(?:vitrohud|agentglass)-bridge-[0-9a-f]{32}$")] private static partial Regex Relative();
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint pid);

    internal static bool IsConfigured => File.Exists(ConnectionPath);
    internal ClaudeConnection(bool requireSource = true)
    {
        if (new FileInfo(ConnectionPath).Length > 16000) throw new IOException();
        configuration = JsonSerializer.Deserialize<Configuration>(File.ReadAllText(ConnectionPath), Wire.Json) ?? throw new IOException();
        if (!Session().IsMatch(configuration.SessionId) || !Relative().IsMatch(configuration.Relative) || !Guid.TryParseExact(configuration.Nonce, "N", out _)) throw new IOException();
        configuration = configuration with { Workspace = Workspace(configuration.Workspace) };
        folder = Path.Combine(configuration.Workspace, configuration.Relative);
        CheckPaths(); if (requireSource) AssertSource();
    }
    private static string Workspace(string value)
    {
        if (!Path.IsPathFullyQualified(value) || value.StartsWith(@"\\", StringComparison.Ordinal) || value.IndexOf(':', 2) >= 0) throw new IOException();
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        if (!Directory.Exists(full)) throw new IOException();
        for (var directory = new DirectoryInfo(full); directory != null; directory = directory.Parent)
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException();
        return full;
    }
    private void CheckPaths()
    {
        if (!Directory.Exists(folder) || (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) throw new IOException();
        Workspace(configuration.Workspace);
        foreach (var name in new[] { "request.json", "response.json", "ready.json", "request.tmp" })
        {
            var path = Path.Combine(folder, name);
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException();
        }
    }
    private void AssertSource()
    {
        if (!IsWindow((nint)configuration.Window)) throw new IOException();
        GetWindowThreadProcessId((nint)configuration.Window, out var pid);
        using var process = Process.GetProcessById(configuration.ProcessId);
        var path = process.MainModule?.FileName ?? "";
        if (pid != configuration.ProcessId || process.StartTime.ToUniversalTime().Ticks != configuration.ProcessStart || !path.StartsWith(@"C:\Program Files\WindowsApps\Claude_", StringComparison.OrdinalIgnoreCase) || !path.EndsWith(@"\app\Claude.exe", StringComparison.OrdinalIgnoreCase)) throw new IOException();
    }
    internal static string Prepare(string workspace, string sessionId, int processId, long window)
    {
        workspace = Workspace(workspace);
        if (!Session().IsMatch(sessionId)) throw new IOException();
        using var process = Process.GetProcessById(processId);
        if (IsConfigured) Disconnect();
        var config = new Configuration(sessionId, workspace, ".vitrohud-bridge-" + Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), processId, process.StartTime.ToUniversalTime().Ticks, window);
        var directory = Directory.CreateDirectory(Path.Combine(workspace, config.Relative));
        var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false);
        var user = WindowsIdentity.GetCurrent().User ?? throw new IOException();
        foreach (var sid in new[] { user, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        directory.SetAccessControl(acl);
        File.WriteAllText(Path.Combine(directory.FullName, "request.json"), "{}");
        File.WriteAllText(Path.Combine(directory.FullName, "response.json"), "{}");
        Directory.CreateDirectory(Path.GetDirectoryName(ConnectionPath)!);
        File.WriteAllText(ConnectionPath, JsonSerializer.Serialize(config, Wire.Json));
        var fileAcl = new FileSecurity(); fileAcl.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { user, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) }) fileAcl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(ConnectionPath).SetAccessControl(fileAcl);
        var connection = new ClaudeConnection(); connection.AssertSource();
        using var resource = typeof(ClaudeConnection).Assembly.GetManifestResourceStream("VitroHUD.Probe.ClaudeBridge.js") ?? throw new IOException();
        using var reader = new StreamReader(resource);
        var scriptConfig = new { sessionId = config.SessionId, workspace = config.Workspace, relative = config.Relative, nonce = config.Nonce };
        var script = Regex.Replace(reader.ReadToEnd(), @"(?m)^\s*//[^\n]*", "");
        return script.Replace("__VITROHUD_CONFIG__", JsonSerializer.Serialize(scriptConfig, Wire.Json)).Replace("\r", "").Replace("\n", " ");
    }
    private JsonElement Request(BridgeRequest request)
    {
        AssertSource(); CheckPaths();
        using var mutex = new Mutex(false, AppPaths.ClaudeLockPrefix + SourceKey);
        var locked = false;
        try
        {
            try { locked = mutex.WaitOne(1200); } catch (AbandonedMutexException) { locked = true; }
            if (!locked) throw new IOException();
            var id = Guid.NewGuid().ToString();
            var payload = new { id, nonce = configuration.Nonce, at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), op = request.Op, text = request.Text, value = request.Value, menuPath = request.MenuPath };
            var temporary = Path.Combine(folder, "request.tmp");
            File.WriteAllText(temporary, JsonSerializer.Serialize(payload, Wire.Json));
            File.Move(temporary, Path.Combine(folder, "request.json"), true);
            var timeout = request.Op == "read" ? 5500 : 12500;
            var watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < timeout)
            {
                AssertSource();
                try
                {
                    var response = Path.Combine(folder, "response.json");
                    if (new FileInfo(response).Length > 2 * 1024 * 1024) throw new IOException();
                    using var document = JsonDocument.Parse(File.ReadAllText(response));
                    var root = document.RootElement;
                    if (String(root, "id") == id && String(root, "nonce") == configuration.Nonce)
                    {
                        if (!root.TryGetProperty("at", out var at) || !at.TryGetInt64(out var time) || Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - time) > 15000) throw new IOException();
                        // Clear both body-bearing files after consumption. A late
                        // response never causes another submission.
                        File.WriteAllText(Path.Combine(folder, "request.json"), "{}");
                        File.WriteAllText(response, "{}");
                        return root.Clone();
                    }
                }
                catch (JsonException) { }
                catch (IOException) { }
                Thread.Sleep(35);
            }
            // Cancel a request that the renderer has not consumed yet.
            File.WriteAllText(Path.Combine(folder, "request.json"), "{}");
            throw new TimeoutException();
        }
        finally { if (locked) mutex.ReleaseMutex(); }
    }
    internal SourceSnapshot Read()
    {
        var response = Request(new("read"));
        if (!Bool(response, "ok")) throw new InvalidOperationException(Error(response));
        return Snapshot(response.GetProperty("data"));
    }
    private SourceSnapshot Snapshot(JsonElement data) => new()
    {
        Provider = "Claude", SourceKey = SourceKey, WindowHandle = configuration.Window, Status = "connected", BackgroundConnected = true,
        Detail = "已连接原会话 · 本机后台连接", Title = String(data, "title"), Model = String(data, "model") + " " + String(data, "effort"),
        Draft = String(data, "draft"), IsGenerating = Bool(data, "busy"), Partial = Bool(data, "partial"), ClientMinimized = IsIconic((nint)configuration.Window),
        Messages = ExtractMessages(data), Capabilities = new(true, Bool(data, "canSend"), false, Bool(data, "canModel"))
    };
    internal BridgeReply Handle(BridgeRequest request)
    {
        if (request.SourceKey != SourceKey) return new(false, Error: "原会话连接已变化，操作已取消。");
        if (request.Op is not ("send" or "model-menu" or "choose-model")) return new(false, Error: "当前操作不可用。");
        if (request.Text.Length > 60000 || request.Value.Length > 500 || request.MenuPath?.Count > 3) return new(false, Error: "请求超出支持范围。");
        try
        {
            var reply = Request(request);
            var choices = Array(reply, "choices").Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).Distinct().ToList();
            return new(Bool(reply, "ok"), reply.TryGetProperty("data", out var data) ? Snapshot(data) : null, Error(reply), choices.Count > 0 ? choices : null);
        }
        catch { return new(false, Error: request.Op == "send" ? "提交结果待确认，请检查原客户端。不会自动重发，侧栏草稿已保留。" : "后台连接未响应，请重新连接原会话。"); }
    }
    internal static List<MessageBlock> ExtractMessages(JsonElement data)
    {
        var result = new List<MessageBlock>(); var budget = 160000; var previousGroup = ""; var seen = new HashSet<string>();
        foreach (var row in Array(data, "entries").TakeLast(100))
        {
            var role = String(row, "role");
            if (role is not ("user" or "assistant")) continue;
            var id = String(row, "id"); if (id.Length == 0 || !seen.Add(id)) continue;
            var parts = new List<MessagePart>();
            foreach (var part in Array(row, "parts"))
            {
                var text = String(part, "text"); if (text.Length > budget) text = text[..budget]; budget -= text.Length;
                if (text.Length == 0) continue;
                if (String(part, "kind") == "reasoning") parts.Add(new("reasoning", text));
                else TranscriptParts.AddBlocks(parts, "body", MarkdownBlocks.Parse(text, role == "assistant"));
            }
            if (parts.Count == 0) continue;
            var group = String(row, "group");
            if (role == "assistant" && group.Length > 0 && group == previousGroup && result.Count > 0 && result[^1].Role == role)
            {
                var combined = new List<MessagePart>(result[^1].Parts ?? []); combined.AddRange(parts);
                result[^1] = TranscriptParts.Block(result[^1].Id, role, combined);
            }
            else result.Add(TranscriptParts.Block(id, role, parts));
            previousGroup = group;
        }
        return result;
    }
    internal static void Disconnect()
    {
        try
        {
            var connection = new ClaudeConnection(false);
            try { connection.Request(new("disconnect")); } catch { }
            connection.CheckPaths();
            // Only known files in our validated direct child; never recurse.
            foreach (var name in new[] { "request.json", "response.json", "ready.json", "request.tmp" }) File.Delete(Path.Combine(connection.folder, name));
            if (!Directory.EnumerateFileSystemEntries(connection.folder).Any()) Directory.Delete(connection.folder);
        }
        catch { }
        File.Delete(ConnectionPath);
    }
    private static string Error(JsonElement reply) { var value = String(reply, "error"); return value.Length <= 500 ? value : "原客户端连接暂时不可用。"; }
    private static bool Bool(JsonElement root, string key) => root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.True;
    private static string String(JsonElement root, string key) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static IEnumerable<JsonElement> Array(JsonElement root, string key) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToArray() : [];
}
