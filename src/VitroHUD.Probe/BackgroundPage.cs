using System.Net.Http;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VitroHUD;

// Connects only to the original client's loopback DevTools endpoint. It never
// creates a conversation or calls a model provider. All commands execute in the
// existing page and use the client's own event handlers and session.
internal sealed class BackgroundPage : IDisposable
{
    private readonly ClientWebSocket socket = new();
    private int sequence;
    public string TargetId { get; }
    private static readonly HttpClient http = new(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(1) };
    private BackgroundPage(string endpoint, string targetId)
    {
        TargetId = targetId;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { socket.ConnectAsync(new Uri(endpoint), deadline.Token).GetAwaiter().GetResult(); }
        catch { socket.Dispose(); throw; }
    }
    public static BackgroundPage? Connect(string provider, string title, int processId, string? targetId = null)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string[] roots = provider switch
        {
            "DeepSeek" => [Path.Combine(roaming, "@deepseek-ai", "dsh-desktop")],
            "Claude" => [Path.Combine(local, "Claude-3p"), Path.Combine(roaming, "Claude")],
            _ => [Path.Combine(roaming, "Codex", "web", "Codex"), Path.Combine(roaming, "ChatGPT", "web", "ChatGPT"), Path.Combine(roaming, "Codex"), Path.Combine(roaming, "ChatGPT"), Path.Combine(local, "Codex"), Path.Combine(local, "ChatGPT")]
        };
        foreach (var root in roots)
        {
            try
            {
                var lines = File.ReadAllLines(Path.Combine(root, "DevToolsActivePort"));
                if (lines.Length < 2 || !int.TryParse(lines[0], out var port) || port is < 1024 or > 65535 || !lines[1].StartsWith("/devtools/browser/")) continue;
                if (!LocalEndpoint.OwnedBy(port, processId)) continue;
                var origin = $"http://127.0.0.1:{port}";
                using var version = JsonDocument.Parse(http.GetStringAsync(origin + "/json/version").GetAwaiter().GetResult());
                if (new Uri(version.RootElement.GetProperty("webSocketDebuggerUrl").GetString()!).AbsolutePath != lines[1]) continue;
                using var targets = JsonDocument.Parse(http.GetStringAsync(origin + "/json/list").GetAwaiter().GetResult());
                var pages = targets.RootElement.EnumerateArray().Where(t => t.GetProperty("type").GetString() == "page" && Eligible(provider, t.GetProperty("url").GetString() ?? "")).ToList();
                var matches = pages.Where(t => targetId != null ? t.GetProperty("id").GetString() == targetId : SameTitle(t.GetProperty("title").GetString() ?? "", title)).ToList();
                if (matches.Count != 1) continue; // Do not guess among windows or unnamed views.
                var endpoint = new Uri(matches[0].GetProperty("webSocketDebuggerUrl").GetString()!);
                if (!endpoint.IsLoopback || endpoint.Port != port || endpoint.Scheme != "ws") continue;
                return new BackgroundPage(endpoint.AbsoluteUri, matches[0].GetProperty("id").GetString()!);
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException or JsonException or TimeoutException or WebSocketException or UriFormatException or OperationCanceledException or KeyNotFoundException) { }
        }
        return null;
    }
    private static bool Eligible(string provider, string url) => provider switch
    {
        "DeepSeek" => url.StartsWith("dsh-app://app/", StringComparison.OrdinalIgnoreCase),
        "Claude" => Regex.IsMatch(url, @"^app://localhost/(?:epitaxy|code|claude-code-desktop)(?:/|$)", RegexOptions.IgnoreCase),
        _ => url.StartsWith("app://", StringComparison.OrdinalIgnoreCase)
    };
    private static bool SameTitle(string left, string right) => Normalize(left) == Normalize(right);
    private static string Normalize(string value) => Regex.Replace(value, @"\s+", " ").Trim();
    public JsonElement Evaluate(string expression)
    {
        var response = Command("Runtime.evaluate", new { expression, returnByValue = true, awaitPromise = true });
        if (response.TryGetProperty("exceptionDetails", out _)) throw new InvalidOperationException("原客户端页面执行未完成");
        return response.GetProperty("result").GetProperty("value").Clone();
    }
    public JsonElement State(string model) => Evaluate(Script + "return state(" + JsonSerializer.Serialize(model) + ");})()");
    public bool Click(string kind, string model, string? value = null, string? expectedIdentity = null, string? expectedDraft = null) => Evaluate(Script + "return click(" + JsonSerializer.Serialize(kind) + "," + JsonSerializer.Serialize(model) + "," + JsonSerializer.Serialize(value) + "," + JsonSerializer.Serialize(expectedIdentity) + "," + JsonSerializer.Serialize(expectedDraft) + ");})()").GetBoolean();
    public List<string> Choices() => Evaluate(Script + "return menu().map(label);})()").EnumerateArray().Select(e => e.GetString()!).Distinct().ToList();
    public List<MessageBlock> ReadMessages(string provider)
    {
        var blocks = Evaluate(Script + "return transcript(" + JsonSerializer.Serialize(provider) + ");})()");
        return ParseMessages(blocks);
    }
    public (JsonElement State, List<MessageBlock> Messages) ReadView(string model, string provider)
    {
        var view = Evaluate(Script + "return {state:state(" + JsonSerializer.Serialize(model) + "),messages:transcript(" + JsonSerializer.Serialize(provider) + ")};})()");
        return (view.GetProperty("state").Clone(), ParseMessages(view.GetProperty("messages")));
    }
    internal static List<MessageBlock> ParseMessages(JsonElement blocks)
    {
        var result = new List<MessageBlock>();
        foreach (var block in blocks.EnumerateArray())
        {
            var parts = new List<MessagePart>();
            foreach (var segment in block.GetProperty("segments").EnumerateArray())
            {
                var text = segment.GetProperty("text").GetString() ?? "";
                var kind = TranscriptParts.Classify(text, segment.GetProperty("control").GetBoolean(), segment.GetProperty("paragraph").GetBoolean(), segment.GetProperty("classes").GetString() ?? "", segment.GetProperty("reasoning").GetBoolean());
                if (segment.TryGetProperty("blocks", out var structured) && structured.ValueKind == JsonValueKind.Array && structured.GetArrayLength() > 0)
                    TranscriptParts.AddBlocks(parts, kind, JsonSerializer.Deserialize<List<FormattedBlock>>(structured.GetRawText(), Wire.Json) ?? []);
                else TranscriptParts.Add(parts, kind, text);
            }
            if (parts.Count > 0) result.Add(TranscriptParts.Block(block.GetProperty("id").GetString()!, block.GetProperty("role").GetString()!, parts));
        }
        return result;
    }
    public bool InsertText(string text, string identity)
    {
        // A controlled editor may commit after this JS task. The caller must
        // verify the exact draft through a fresh read before any send action.
        return Evaluate(Script + "const accepted=insert(" + JsonSerializer.Serialize(text) + "," + JsonSerializer.Serialize(identity) + "); return accepted||insertionAttempted;})()").GetBoolean();
    }
    private JsonElement Command(string method, object args) => CommandAsync(method, args).GetAwaiter().GetResult();
    private async Task<JsonElement> CommandAsync(string method, object args)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var id = ++sequence;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { id, method, @params = args });
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, deadline.Token);
        var buffer = new byte[16384];
        while (true)
        {
            using var body = new MemoryStream(); WebSocketReceiveResult received;
            do { received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), deadline.Token); if (received.MessageType == WebSocketMessageType.Close) throw new WebSocketException("后台连接已关闭"); body.Write(buffer, 0, received.Count); if (body.Length > 4_000_000) throw new InvalidOperationException("后台响应过大"); } while (!received.EndOfMessage);
            using var message = JsonDocument.Parse(body.ToArray());
            if (!message.RootElement.TryGetProperty("id", out var replyId) || replyId.GetInt32() != id) continue;
            if (message.RootElement.TryGetProperty("error", out _)) throw new InvalidOperationException("后台命令未被客户端接受");
            return message.RootElement.GetProperty("result").Clone();
        }
    }
    public void Dispose() => socket.Dispose();
    private static readonly string Script = LoadScript();
    private static string LoadScript()
    {
        using var resource = typeof(BackgroundPage).Assembly.GetManifestResourceStream("VitroHUD.Probe.PageScript.js")
            ?? throw new InvalidOperationException("缺少后台适配资源");
        using var reader = new StreamReader(resource);
        return reader.ReadToEnd();
    }
}
