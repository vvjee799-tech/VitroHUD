using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using VitroHUD;
using Microsoft.Win32.SafeHandles;

namespace VitroHUD.Probe;

// This connects to the running desktop client's existing thread owner. It does
// not start an app server, create a conversation, or use a model API.
internal sealed class CodexPipe : IDisposable
{
    private readonly NamedPipeClientStream pipe = new(".", "codex-ipc", PipeDirection.InOut, PipeOptions.Asynchronous);
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> pending = new();
    private readonly object stateGate = new();
    private readonly AutoResetEvent changed = new(false);
    private readonly int processId;
    private readonly long processStart;
    private readonly nint window;
    private readonly string conversation;
    private string client = "initializing-client", owner = "";
    private JsonElement state;
    private long generation;
    private volatile bool unavailable, disposed;
    internal string SourceKey => Wire.Hash($"ChatGPT|ipc|{processId}|{processStart}|{owner}|{conversation}");
    internal static readonly string ConnectionPath = AppPaths.FilePath("chatgpt-connection.json");

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);

    internal CodexPipe(string conversationId)
    {
        if (!Guid.TryParseExact(conversationId, "D", out var id)) throw new InvalidOperationException("请使用现有 Codex 会话的链接或 ID。");
        conversation = id.ToString();
        try
        {
            pipe.Connect(1200);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid)) throw new IOException();
            using var process = Process.GetProcessById((int)pid);
            var path = process.MainModule?.FileName ?? "";
            // Verify the pipe server is the client, regardless of installation root.
            if (!ClientDiscovery.IsExecutable("ChatGPT", path)) throw new IOException();
            processId = process.Id; processStart = process.StartTime.ToUniversalTime().Ticks; window = process.MainWindowHandle;
            if (window == 0) throw new IOException();
            _ = Task.Run(ReadLoop);
            var initialized = Request("initialize", 0, new { clientType = "agentglass" });
            client = String(initialized.GetProperty("result"), "clientId");
            if (client.Length == 0) throw new IOException();
            var found = Request("thread-owner-discovery", 1, new { hostId = "local", conversationId = conversation });
            owner = String(found, "handledByClientId");
            if (owner.Length == 0 || !found.GetProperty("result").TryGetProperty("supportsUntrustedAppInput", out var supported) || supported.ValueKind != JsonValueKind.True) throw new IOException();
            Refresh();
        }
        catch { Dispose(); throw; }
    }

    internal static string ParseConversation(string value)
    {
        value = value.Trim();
        if (Guid.TryParseExact(value, "D", out var id)) return id.ToString();
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "codex" && uri.Host is "thread" or "threads" && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment) && Guid.TryParseExact(uri.AbsolutePath.Trim('/'), "D", out id)) return id.ToString();
        return "";
    }
    internal static string ConfiguredConversation()
    {
        try
        {
            if (new FileInfo(ConnectionPath).Length > 4096) return "";
            using var document = JsonDocument.Parse(File.ReadAllText(ConnectionPath));
            return ParseConversation(String(document.RootElement, "conversationId"));
        }
        catch { return ""; }
    }
    internal static void Configure(string id)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConnectionPath)!);
        var temporary = ConnectionPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new { conversationId = id }, Wire.Json));
        File.Move(temporary, ConnectionPath, true);
    }

    private void AssertOwner()
    {
        if (unavailable || disposed || !pipe.IsConnected || !IsWindow(window)) throw new IOException();
        GetWindowThreadProcessId(window, out var pid);
        using var process = Process.GetProcessById(processId);
        if (pid != processId || process.StartTime.ToUniversalTime().Ticks != processStart) throw new IOException();
    }
    private void Write(object message)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, Wire.Json);
        if (bytes.Length > 1024 * 1024) throw new IOException();
        writeGate.Wait(lifetime.Token);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            deadline.CancelAfter(1500);
            pipe.WriteAsync(BitConverter.GetBytes(bytes.Length), deadline.Token).AsTask().GetAwaiter().GetResult();
            pipe.WriteAsync(bytes, deadline.Token).AsTask().GetAwaiter().GetResult();
            pipe.FlushAsync(deadline.Token).GetAwaiter().GetResult();
        }
        finally { writeGate.Release(); }
    }
    private JsonElement Request(string method, int version, object parameters, bool targeted = false)
    {
        if (method != "initialize") AssertOwner();
        var id = Guid.NewGuid().ToString();
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!pending.TryAdd(id, completion)) throw new IOException();
        try
        {
            var frame = new Dictionary<string, object?> { ["type"] = "request", ["requestId"] = id, ["sourceClientId"] = client, ["version"] = version, ["method"] = method, ["params"] = parameters, ["timeoutMs"] = 4000 };
            if (targeted) frame["targetClientId"] = owner;
            Write(frame);
            var response = completion.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            if (String(response, "resultType") != "success" || String(response, "method") != method || targeted && String(response, "handledByClientId") != owner) throw new IOException();
            return response;
        }
        finally { pending.TryRemove(id, out _); }
    }
    private async Task ReadLoop()
    {
        try
        {
            var header = new byte[4];
            while (!lifetime.IsCancellationRequested)
            {
                await pipe.ReadExactlyAsync(header, lifetime.Token);
                var count = BitConverter.ToInt32(header);
                if (count is < 1 or > 8 * 1024 * 1024) throw new IOException();
                var bytes = new byte[count];
                await pipe.ReadExactlyAsync(bytes, lifetime.Token);
                using var document = JsonDocument.Parse(bytes, new() { MaxDepth = 100 });
                var frame = document.RootElement;
                switch (String(frame, "type"))
                {
                    case "response":
                        if (pending.TryGetValue(String(frame, "requestId"), out var request)) request.TrySetResult(frame.Clone());
                        break;
                    case "client-discovery-request":
                        Write(new { type = "client-discovery-response", requestId = String(frame, "requestId"), response = new { canHandle = false } });
                        break;
                    case "broadcast":
                        if (String(frame, "method") == "client-status-changed" && frame.TryGetProperty("params", out var status) && String(status, "clientId") == owner && String(status, "status") == "disconnected") throw new IOException();
                        if (String(frame, "method") != "thread-stream-state-changed" || String(frame, "sourceClientId") != owner || !frame.TryGetProperty("version", out var version) || version.GetInt32() != 11 || !frame.TryGetProperty("params", out var data) || String(data, "hostId") != "local" || String(data, "conversationId") != conversation || !data.TryGetProperty("change", out var change) || String(change, "type") != "snapshot" || !change.TryGetProperty("conversationState", out var next) || String(next, "id") != conversation) break;
                        lock (stateGate) { state = next.Clone(); generation++; }
                        changed.Set();
                        break;
                }
            }
        }
        catch
        {
            unavailable = true;
            foreach (var request in pending.Values) request.TrySetException(new IOException());
            changed.Set();
        }
    }
    private void Follow(bool following) => Write(new { type = "broadcast", method = "thread-stream-following-changed", sourceClientId = client, targetClientIds = new[] { owner }, version = 1, @params = new { hostId = "local", conversationId = conversation, following } });
    private JsonElement Refresh()
    {
        AssertOwner();
        long previous; lock (stateGate) previous = generation;
        Follow(false); Follow(true);
        var deadline = Stopwatch.StartNew();
        while (deadline.ElapsedMilliseconds < 2300)
        {
            AssertOwner();
            lock (stateGate) if (generation > previous && String(state, "id") == conversation) return state;
            changed.WaitOne(50);
        }
        throw new IOException();
    }
    internal SourceSnapshot Read()
    {
        var current = Refresh();
        var messages = ExtractMessages(current);
        var active = Active(current);
        var operable = !active && NoRequests(current) && String(current, "resumeState") == "resumed";
        var model = String(current, "latestModel"); var effort = String(current, "latestReasoningEffort");
        return new()
        {
            Provider = "ChatGPT", SourceKey = SourceKey, WindowHandle = window, Status = "connected", Title = String(current, "title"),
            Detail = "已连接选定的原会话 · 本机后台连接", Model = model + (effort.Length > 0 ? " " + effort : ""),
            BackgroundConnected = true, ClientMinimized = IsIconic(window), IsGenerating = active, Partial = true,
            Messages = messages, Capabilities = new(true, operable, false, operable && Catalog().Count > 0 && String(current, "modelProvider") == "openai")
        };
    }
    private static bool Active(JsonElement current) => current.TryGetProperty("threadRuntimeStatus", out var status) && String(status, "type") == "active" || Turns(current).Any(t => String(t, "status") is "inProgress" or "in_progress");
    private static bool NoRequests(JsonElement current) => current.TryGetProperty("requests", out var requests) && requests.ValueKind == JsonValueKind.Array && requests.GetArrayLength() == 0;
    internal BridgeReply Handle(BridgeRequest request)
    {
        if (request.SourceKey != SourceKey) return new(false, Error: "原会话连接已变化，操作已取消。");
        var current = Refresh();
        if (String(current, "resumeState") != "resumed" || Active(current) || !NoRequests(current)) return new(false, Error: "原会话正在处理消息或等待操作，请先在原客户端处理。");
        if (request.Op == "model-menu") return new(true, Choices: ["模型 ▸", "推理强度 ▸"]);
        if (request.Op == "choose-model") return ChooseModel(current, request);
        if (request.Op != "send" || string.IsNullOrWhiteSpace(request.Text) || request.Text.Length > 60000) return new(false, Error: "当前操作不可用。");
        // This path never edits or clears the original client's composer.
        // The owner applies the existing thread's model, permissions and workspace.
        var messageId = Guid.NewGuid().ToString();
        var attempted = false;
        try
        {
            AssertOwner(); attempted = true;
            Request("thread-follower-start-turn", 2, new { conversationId = conversation, turnStart = new { request = new { threadId = conversation, input = new[] { new { type = "text", text = request.Text, text_elements = System.Array.Empty<object>() } }, clientUserMessageId = messageId }, context = new { inheritThreadSettings = true } } }, true);
            var receiptWait = Stopwatch.StartNew();
            while (receiptWait.ElapsedMilliseconds < 5500)
            {
                var after = Refresh();
                if (Turns(after).Any(turn => turn.TryGetProperty("params", out var parameters) && String(parameters, "clientUserMessageId") == messageId) || Turns(after).SelectMany(t => Array(t, "items")).Any(item => String(item, "type") == "userMessage" && String(item, "clientId") == messageId)) return new(true);
                Thread.Sleep(180);
            }
        }
        catch when (attempted) { return new(false, Error: "提交结果待确认，请检查原客户端。不会自动重发，侧栏草稿已保留。"); }
        return new(false, Error: "尚未核对到提交回执，请检查原客户端。不会自动重发，侧栏草稿已保留。");
    }

    private sealed record ModelOption(string Id, string Name, List<string> Efforts);
    private static List<ModelOption> Catalog()
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "models_cache.json");
            if (new FileInfo(path).Length > 4 * 1024 * 1024) return [];
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return Array(document.RootElement, "models").Where(m => String(m, "visibility") != "hide" && String(m, "visibility") != "hidden").Select(m => new ModelOption(String(m, "slug"), String(m, "display_name"), Array(m, "supported_reasoning_levels").Select(l => String(l, "effort")).Where(e => e.Length > 0).ToList())).Where(m => m.Id.Length > 0 && m.Name.Length > 0).ToList();
        }
        catch { return []; }
    }
    private BridgeReply ChooseModel(JsonElement current, BridgeRequest request)
    {
        if (String(current, "modelProvider") != "openai") return new(false, Error: "当前提供方的模型目录尚未确认，请在原客户端选择。");
        var catalog = Catalog(); var oldModel = String(current, "latestModel"); var oldEffort = String(current, "latestReasoningEffort");
        if (request.MenuPath?.Count is null or 0)
        {
            if (request.Value == "模型 ▸") return new(true, Choices: catalog.Select(m => m.Name).Distinct().ToList());
            if (request.Value == "推理强度 ▸") return new(true, Choices: catalog.FirstOrDefault(m => m.Id == oldModel)?.Efforts ?? []);
            return new(false, Error: "模型目录已变化，请重新打开。");
        }
        string model, effort;
        if (request.MenuPath.Count != 1) return new(false, Error: "模型选择路径无效。");
        if (request.MenuPath[0] == "模型 ▸")
        {
            var matches = catalog.Where(m => m.Name == request.Value).ToList();
            if (matches.Count != 1) return new(false, Error: "该模型未在原客户端目录中确认。");
            model = matches[0].Id;
            if (oldEffort.Length > 0 && !matches[0].Efforts.Contains(oldEffort)) return new(false, Error: "目标模型不支持当前推理等级，请先在原客户端调整。");
            effort = oldEffort;
        }
        else if (request.MenuPath[0] == "推理强度 ▸" && catalog.FirstOrDefault(m => m.Id == oldModel)?.Efforts.Contains(request.Value) == true) { model = oldModel; effort = request.Value; }
        else return new(false, Error: "该推理等级未在原客户端目录中确认。");
        var settings = new Dictionary<string, object?> { ["model"] = model };
        if (effort.Length > 0) settings["effort"] = effort;
        var response = Request("thread-follower-update-thread-settings", 2, new { conversationId = conversation, threadSettings = settings, condition = new { ifModelEquals = oldModel, ifEffortEquals = oldEffort.Length > 0 ? oldEffort : null } }, true);
        if (!response.GetProperty("result").TryGetProperty("applied", out var applied) || applied.ValueKind != JsonValueKind.True) return new(false, Error: "原会话设置已变化，未覆盖原选择。");
        var verified = Request("thread-follower-read-model-settings", 1, new { conversationId = conversation }, true).GetProperty("result").GetProperty("settings");
        return String(verified, "model") == model && (effort.Length == 0 || String(verified, "reasoningEffort") == effort) ? new(true) : new(false, Error: "模型设置尚未核对，请检查原客户端。");
    }

    private static IEnumerable<JsonElement> Turns(JsonElement current)
    {
        if (current.TryGetProperty("turnHistory", out var history) && String(history, "kind") == "canonical" && history.TryGetProperty("history", out var data) && data.TryGetProperty("entitiesByKey", out var entities) && entities.ValueKind == JsonValueKind.Object)
        {
            var ordered = new List<JsonElement>(); var seen = new HashSet<string>();
            foreach (var entry in Array(data, "islands").SelectMany(i => Array(i, "entries")))
            {
                var key = String(entry, "key");
                if (key.Length > 0 && seen.Add(key) && entities.TryGetProperty(key, out var turn)) ordered.Add(turn);
            }
            if (ordered.Count > 0) return ordered;
            return entities.EnumerateObject().Select(p => p.Value).OrderBy(t => t.TryGetProperty("turnStartedAtMs", out var time) && time.TryGetInt64(out var value) ? value : 0).ToList();
        }
        return Array(current, "turns");
    }
    internal static List<MessageBlock> ExtractMessages(JsonElement current)
    {
        var result = new List<MessageBlock>(); var budget = 160000;
        var items = Turns(current).SelectMany(t => Array(t, "items")).Where(i => String(i, "type") is "userMessage" or "agentMessage" or "reasoning").TakeLast(150).ToList();
        foreach (var item in items.AsEnumerable().Reverse())
        {
            var type = String(item, "type"); var role = type == "userMessage" ? "user" : "assistant";
            var text = type switch { "userMessage" => string.Join("\n", Array(item, "content").Where(c => String(c, "type") is "text" or "input_text").Select(c => String(c, "text"))), "agentMessage" => String(item, "text"), _ => string.Join("\n", Array(item, "summary").Select(c => c.ValueKind == JsonValueKind.String ? c.GetString()! : String(c, "text"))) };
            if (text.Length == 0) continue;
            if (budget <= 0) break;
            if (text.Length > budget) text = text[..budget];
            budget -= text.Length;
            var parts = new List<MessagePart>();
            if (type == "reasoning") parts.Add(new("reasoning", text));
            else TranscriptParts.AddBlocks(parts, "body", MarkdownBlocks.Parse(text, role == "assistant"));
            result.Add(TranscriptParts.Block(String(item, "id"), role, parts));
            if (result.Count >= 100) break;
        }
        result.Reverse(); return result;
    }
    private static IEnumerable<JsonElement> Array(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToArray() : [];
    private static string String(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    public void Dispose()
    {
        if (disposed) return;
        try { if (owner.Length > 0 && !unavailable && pipe.IsConnected) Follow(false); } catch { }
        disposed = true; unavailable = true; lifetime.Cancel(); pipe.Dispose(); changed.Set();
    }
}
