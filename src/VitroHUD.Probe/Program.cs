using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using VitroHUD;

internal static partial class Program
{
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindow callback, nint parameter);
    private delegate bool EnumWindow(nint handle, nint parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint handle, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint handle);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint handle);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint handle);
    private static string Provider = "ChatGPT";
    private static readonly TreeWalker Walker = TreeWalker.ControlViewWalker;
    private sealed record PageBinding(SourceSnapshot Source, int ProcessId, string TargetId, string Identity);
    private static PageBinding? backgroundBinding;
    private static VitroHUD.Probe.CodexPipe? codexPipe;
    private static string codexConversation = "";

    [MTAThread]
    private static int Main(string[] args)
    {
        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = new UTF8Encoding(false);
        Provider = args.FirstOrDefault(a => a is "Claude" or "ChatGPT" or "DeepSeek") ?? "ChatGPT";
        if (args.Contains("--glass-geometry")) { Console.WriteLine(JsonSerializer.Serialize(WindowGeometry.Read())); return 0; }
        if (args.Contains("--launch-background"))
        {
            var path = args.LastOrDefault(a => a.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
            if (path == null || !System.IO.File.Exists(path) || !string.Equals(System.IO.Path.GetFileNameWithoutExtension(path), Provider switch { "DeepSeek" => "DeepSeek Harness", "Claude" => "Claude", _ => "ChatGPT" }, StringComparison.OrdinalIgnoreCase)) return 2;
            Console.WriteLine(JsonSerializer.Serialize(new { startedProcess = ClientActivation.Start(path) })); return 0;
        }
        if (args.Contains("--self-test")) return SelfTest();
        if (args.Contains("--once")) { Emit(Read()); return 0; }
        if (args.Contains("--inspect-editor")) { Inspect(true); return 0; }
        if (args.Contains("--inspect-metadata")) { InspectMetadata(); return 0; }
        if (args.Contains("--inspect")) { Inspect(); return 0; }
        string? line;
        while ((line = Console.ReadLine()) != null)
        {
            try
            {
                var request = JsonSerializer.Deserialize<BridgeRequest>(line, Wire.Json);
                Emit(request == null ? new(false, Error: "无效请求") : Handle(request));
            }
            catch (Exception ex) { Emit(new(false, Error: SafeError(ex))); }
        }
        return 0;
    }
    private static void Emit(BridgeReply reply) => Console.WriteLine(JsonSerializer.Serialize(reply, Wire.Json));
    private static string SafeError(Exception ex)
    {
        Console.Error.WriteLine($"{ex.GetType().Name} (0x{ex.HResult:X8})");
        return ex is ElementNotAvailableException ? "客户端视图已变更，请等待重新连接" : "客户端暂时未响应，请重试连接";
    }

    private static List<AutomationElement> Windows()
    {
        var processName = Provider switch { "Claude" => "claude", "DeepSeek" => "DeepSeek Harness", _ => "ChatGPT" };
        var pids = Process.GetProcessesByName(processName).Select(p => p.Id).ToHashSet();
        var windows = new List<AutomationElement>();
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var pid);
            if (pids.Contains((int)pid) && IsWindowVisible(h))
            {
                try { var e = AutomationElement.FromHandle(h); if (IsIconic(h) || e.Current.BoundingRectangle.Width > 250) windows.Add(e); } catch { }
            }
            return true;
        }, 0);
        return windows;
    }

    private static List<AutomationElement> Walk(AutomationElement root, int limit = 3000, int maxDepth = 32, bool messageOnly = false, AutomationElement? excluded = null, bool rawView = false)
    {
        var tree = rawView ? TreeWalker.RawViewWalker : Walker;
        var result = new List<AutomationElement>();
        var excludedId = excluded?.GetRuntimeId();
        var queue = new LinkedList<(AutomationElement Element, int Depth)>();
        queue.AddLast((root, 0)); var watch = Stopwatch.StartNew();
        while (queue.Count > 0 && result.Count < limit && watch.ElapsedMilliseconds < 3500)
        {
            var (element, depth) = queue.First!.Value; queue.RemoveFirst();
            if (excludedId != null && element.GetRuntimeId().SequenceEqual(excludedId)) continue;
            if (messageOnly && (Type(element, ControlType.ToolBar) || Type(element, ControlType.Button) || Type(element, ControlType.StatusBar) || Class(element).Contains("sr-only") || Class(element).Contains("group/morph"))) continue;
            result.Add(element);
            if (depth >= maxDepth) continue;
            try
            {
                var children = new List<AutomationElement>();
                for (var child = tree.GetFirstChild(element); child != null && children.Count + queue.Count < limit; child = tree.GetNextSibling(child)) children.Add(child);
                if (messageOnly) for (var i = children.Count - 1; i >= 0; i--) queue.AddFirst((children[i], depth + 1));
                else foreach (var child in children) queue.AddLast((child, depth + 1));
            }
            catch (ElementNotAvailableException) { }
        }
        return result;
    }
    private static string Name(AutomationElement e) { try { return e.Current.Name ?? ""; } catch { return ""; } }
    private static string Class(AutomationElement e) { try { return e.Current.ClassName ?? ""; } catch { return ""; } }
    private static bool Type(AutomationElement e, ControlType type) { try { return e.Current.ControlType == type; } catch { return false; } }
    private static bool Enabled(AutomationElement? e) { try { return e?.Current.IsEnabled == true; } catch { return false; } }
    private static string ClaudeConversationId(string address) => ClaudeSession().Match(address).Groups[1].Value;
    [GeneratedRegex(@"^app://localhost/epitaxy/(local_[0-9a-f-]{36}|[0-9a-f-]{36})(?:[?#]|$)", RegexOptions.IgnoreCase)] private static partial Regex ClaudeSession();

    private sealed record Context(AutomationElement Window, AutomationElement? Document, AutomationElement? Conversation, List<AutomationElement> Elements, string Key, bool StableIdentity, string Title);
    private static Context? Locate(string expectedKey = "")
    {
        var windows = Windows();
        foreach (var window in windows)
        {
            var elements = Walk(window);
            var document = elements.LastOrDefault(e => Type(e, ControlType.Document) && Name(e).Length > 0) ?? elements.LastOrDefault(e => Type(e, ControlType.Document));
            var title = document == null ? Name(window) : Name(document);
            var address = "";
            if (document?.TryGetCurrentPattern(ValuePattern.Pattern, out var raw) == true) address = ((ValuePattern)raw).Current.Value;
            if (address.StartsWith("devtools://", StringComparison.OrdinalIgnoreCase)) continue;
            var selected = elements.FirstOrDefault(e => Type(e, ControlType.TreeItem) && e.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var p) && ((SelectionItemPattern)p).Current.IsSelected);
            var identity = selected == null ? address + "|" + title : string.Join("-", selected.GetRuntimeId());
            var stableIdentity = selected != null;
            var canonical = Regex.Match(address, @"/(?:local|c|chat|chats|thread|threads)/([0-9a-f]{8}-[0-9a-f-]{27,})(?:[/?#]|$)", RegexOptions.IgnoreCase);
            if (Provider == "ChatGPT" && canonical.Success) { identity = canonical.Groups[1].Value; stableIdentity = true; }
            if (Provider == "DeepSeek")
            {
                var chatTitle = title.Replace(" — DeepSeek Harness", "");
                var matching = elements.Where(e => Type(e, ControlType.TreeItem) && (Name(e) == chatTitle || Name(e).StartsWith(chatTitle + " ", StringComparison.Ordinal))).ToList();
                // Selection/focus changes while a native menu opens. Bind the uniquely named conversation instead.
                identity = address + "|" + title;
                stableIdentity = chatTitle.Length > 0 && matching.Count == 1;
            }
            if (Provider == "Claude")
            {
                var sessionId = ClaudeConversationId(address);
                stableIdentity = sessionId.Length > 0;
                identity = stableIdentity ? sessionId : address + "|" + title;
            }
            var conversation = elements.FirstOrDefault(e => Class(e).Contains("thread-scroll-container"))
                ?? elements.FirstOrDefault(e => Class(e).Contains("chat-messages") || Class(e).Contains("conversation-messages") || Class(e).Contains("messages-container"))
                ?? elements.FirstOrDefault(e => Name(e) is "对话" or "Conversation" && Type(e, ControlType.Group));
            if (Provider == "Claude") conversation = elements.FirstOrDefault(e => Name(e) == "Chat messages") ?? conversation;
            if (conversation == null && Provider is "DeepSeek" or "Claude" && document != null && elements.Any(e => Type(e, ControlType.Edit))) conversation = document;
            if (Provider == "ChatGPT" && conversation != null) identity += "|view:" + string.Join("-", conversation.GetRuntimeId());
            var key = Wire.Hash($"{Provider}|{window.Current.ProcessId}|{window.Current.NativeWindowHandle}|{identity}");
            if (expectedKey.Length > 0 && key != expectedKey) continue;
            return new(window, document, conversation, elements, key, stableIdentity, title);
        }
        return null;
    }
    internal static bool IsComposer(string provider, string name, string className)
    {
        if (name.Contains("搜索") || name.Contains("search", StringComparison.OrdinalIgnoreCase) || className.Contains("search", StringComparison.OrdinalIgnoreCase)) return false;
        return provider switch
        {
            "ChatGPT" => className.Contains("ProseMirror") && name is "随心输入" or "和 Codex 一起工作" or "Work with Codex" or "Message ChatGPT" or "向 ChatGPT 发送消息" || name.StartsWith("询问任何问题"),
            "DeepSeek" => name.StartsWith("发消息或创建任务") || name.StartsWith("描述你想要构建的内容") || name.StartsWith("Send a message or create a task"),
            "Claude" => name is "Prompt" or "Reply to Claude..." or "回复 Claude…" || className.Contains("ProseMirror") && name.Contains("Reply"),
            _ => false
        };
    }
    private static AutomationElement? Editor(Context c) => c.Elements.LastOrDefault(e => Type(e, ControlType.Edit) && IsComposer(Provider, Name(e), Class(e)));
    private static string DraftValue(AutomationElement? editor)
    {
        if (editor?.TryGetCurrentPattern(ValuePattern.Pattern, out var value) != true) return "";
        var text = ((ValuePattern)value).Current.Value;
        if (Provider == "ChatGPT" && text.Trim() == Name(editor))
        {
            // Chromium includes CSS placeholder text in ValuePattern. A real draft with
            // the same words has no empty-paragraph marker or trailing break.
            var nodes = Walk(editor, 100, 12, rawView: true);
            var markedEmpty = nodes.Any(e => Type(e, ControlType.Group) && Class(e).Split(' ').Contains("placeholder"))
                && nodes.Any(e => Class(e).Contains("ProseMirror-trailingBreak") && string.IsNullOrWhiteSpace(Name(e)));
            var placeholderOnly = nodes.Where(e => Type(e, ControlType.Text)).All(e => string.IsNullOrWhiteSpace(Name(e)) || Name(e).Trim() == Name(editor));
            if (IsEmptyPlaceholder(text, Name(editor), markedEmpty, placeholderOnly)) return "";
        }
        return text;
    }
    private static bool IsEmptyPlaceholder(string value, string label, bool markedEmpty, bool placeholderOnly) =>
        label.Length > 0 && value.Trim() == label && markedEmpty && placeholderOnly;
    private static bool? TextReadOnly(AutomationElement root, AutomationElement child)
    {
        try
        {
            if (root.TryGetCurrentPattern(TextPattern.Pattern, out var raw)) return ((TextPattern)raw).RangeFromChild(child).GetAttributeValue(TextPattern.IsReadOnlyAttribute) as bool?;
        }
        catch (Exception) { }
        return null;
    }
    private static AutomationElement? SendButton(Context c) => c.Elements.LastOrDefault(e => Type(e, ControlType.Button) && Name(e) is "发送" or "发送消息" or "Send" or "Send message" or "提交" or "Submit");
    private static AutomationElement? StopButton(Context c) => c.Elements.LastOrDefault(e => Type(e, ControlType.Button) && Name(e) is "停止" or "停止生成" or "Stop" or "Stop generating" or "停止响应");
    private static AutomationElement? ModelButton(Context c) => c.Elements.LastOrDefault(e => Type(e, ControlType.Button) && (ModelLabel().IsMatch(Name(e)) || Name(e).StartsWith("选择模型，当前") || Name(e).StartsWith("Choose model") || Name(e).StartsWith("Model:")));
    [GeneratedRegex(@"^(GPT[-\s]|Claude\s|Opus\s|Sonnet\s|Haiku\s|DeepSeek[-\s])", RegexOptions.IgnoreCase)] private static partial Regex ModelLabel();
    private static List<AutomationElement> MenuChoices(Context c)
    {
        // Scope to an open popup, never the app's native File/Edit menu bar.
        var menus = Walk(c.Window).Where(e => Type(e, ControlType.Menu) && !e.Current.IsOffscreen && e.Current.BoundingRectangle.Width > 0).ToList();
        var menu = menus.LastOrDefault();
        return menu == null ? [] : Walk(menu, 250, 12).Where(e => (Type(e, ControlType.MenuItem) || Type(e, ControlType.RadioButton)) && Enabled(e) && !e.Current.IsOffscreen && Name(e).Length > 0)
            .OrderBy(e => e.Current.BoundingRectangle.Top).ThenBy(e => e.Current.BoundingRectangle.Left).ToList();
    }
    private static BridgeReply MenuReply(Context c, string error = "")
    {
        var choices = MenuChoices(c).Select(Name).Distinct().ToList();
        return new(choices.Count > 0, Choices: choices, Error: choices.Count > 0 ? "" : error);
    }
    private static bool OpenModelRoot(Context c)
    {
        var button = ModelButton(c);
        if (button?.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke) == true)
        {
            ((InvokePattern)invoke).Invoke(); Thread.Sleep(160);
            if (MenuChoices(c).Count == 0) ((InvokePattern)invoke).Invoke();
        }
        else if (button?.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expand) == true)
        {
            var pattern = (ExpandCollapsePattern)expand;
            if (pattern.Current.ExpandCollapseState == ExpandCollapseState.Expanded) { pattern.Collapse(); Thread.Sleep(160); }
            pattern.Expand();
        }
        else return false;
        var watch = Stopwatch.StartNew();
        do { Thread.Sleep(100); if (MenuChoices(c).Count > 0) return true; } while (watch.ElapsedMilliseconds < 2500);
        return false;
    }
    private static bool PickMenuChoice(Context c, string value)
    {
        var choice = MenuChoices(c).FirstOrDefault(e => Name(e) == value);
        if (choice?.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expand) == true && ((ExpandCollapsePattern)expand).Current.ExpandCollapseState != ExpandCollapseState.LeafNode) ((ExpandCollapsePattern)expand).Expand();
        else if (choice?.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selection) == true) ((SelectionItemPattern)selection).Select();
        else if (choice?.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke) == true) ((InvokePattern)invoke).Invoke();
        else return false;
        Thread.Sleep(160); return true;
    }

    private static SourceSnapshot Snapshot(Context c)
    {
        var editor = Editor(c); var send = SendButton(c); var stop = StopButton(c); var model = ModelButton(c);
        var draft = DraftValue(editor);
        var messages = c.Conversation == null ? new List<MessageBlock>() : Extract(c, editor);
        var minimized = IsIconic(c.Window.Current.NativeWindowHandle);
        var operable = !minimized;
        var providerError = c.Elements.Any(e => Type(e, ControlType.Text) && (Name(e).StartsWith("Can’t reach ") || Name(e).StartsWith("Can't reach ") || Name(e).StartsWith("The provider didn’t respond")));
        var cooldown = Provider == "DeepSeek" && c.Elements.Any(e => Type(e, ControlType.Text) && Name(e).Contains("per-model cooldown"));
        return new()
        {
            Provider = Provider, SourceKey = c.Key, WindowHandle = c.Window.Current.NativeWindowHandle,
            Status = providerError ? "client-error" : minimized ? "paused" : c.Conversation == null ? "unrecognized" : cooldown ? "upstream-error" : "connected",
            Detail = providerError ? "官方客户端无法连接其提供方，请在原客户端恢复连接" : minimized ? "客户端已最小化 · 尚未连接可用的后台操作方式" : c.Conversation == null ? "已找到客户端，等待打开会话" : !c.StableIdentity ? "读取模式 · 当前会话身份尚未确认" : cooldown ? "原客户端本轮失败 · 模型暂不可用" : "已连接官方客户端 · 已加载内容",
            Title = c.Title,
            Model = model == null ? "由客户端控制" : Name(model).Replace("选择模型，当前 ", "").Replace("，推理等级 ", " ").Replace("Model: ", ""), Draft = draft, IsGenerating = stop != null && Enabled(stop), Partial = true,
            Messages = messages,
            ClientMinimized = minimized,
            Capabilities = new(c.Conversation != null, operable && c.StableIdentity && !providerError && editor?.TryGetCurrentPattern(ValuePattern.Pattern, out _) == true && (Provider == "Claude" || Provider == "ChatGPT" && c.Conversation != null || send?.TryGetCurrentPattern(InvokePattern.Pattern, out _) == true), operable && c.StableIdentity && stop?.TryGetCurrentPattern(InvokePattern.Pattern, out _) == true && Enabled(stop), operable && c.StableIdentity && (model?.TryGetCurrentPattern(InvokePattern.Pattern, out _) == true || model?.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out _) == true))
        };
    }

    private static List<MessageBlock> Extract(Context c, AutomationElement? editor)
    {
        var conversation = c.Conversation!;
        if (Provider == "Claude" && Name(conversation) == "Chat messages")
        {
            var articles = Walk(conversation).Where(e => Type(e, ControlType.Group) && Regex.IsMatch(Name(e), @"^Message \d+$")).TakeLast(100);
            var result = new List<MessageBlock>();
            foreach (var article in articles)
            {
                var heading = Walk(article, 600).FirstOrDefault(e =>
                    (Name(e).StartsWith("You said:") || Name(e).StartsWith("Claude responded:") || Name(e).StartsWith("你说："))
                    && (Class(e).Contains("sr-only") || e.Current.LocalizedControlType.Contains("heading", StringComparison.OrdinalIgnoreCase) || e.Current.LocalizedControlType.Contains("标题")));
                var parts = ExtractArticleParts(article, heading);
                var text = string.Join("\n", parts.Select(p => p.Text)).Trim();
                var role = heading == null ? "transcript" : UserHeading(Name(heading)) ? "user" : "assistant";
                if (text.Length > 0) result.Add(TranscriptParts.Block(Wire.Hash(Name(article)), role, parts));
            }
            return result;
        }
        if (Provider == "DeepSeek" || conversation == c.Document) return ExtractByRegion(c, editor);
        if (Provider == "ChatGPT")
        {
            var clean = ExtractChatGpt(conversation, editor);
            if (clean.Count > 0) return clean;
        }
        if (c.Document?.TryGetCurrentPattern(TextPattern.Pattern, out var documentText) == true)
        {
            try
            {
                var pattern = (TextPattern)documentText;
                var range = pattern.RangeFromChild(conversation);
                if (editor != null) { var inputRange = pattern.RangeFromChild(editor); range.MoveEndpointByRange(TextPatternRangeEndpoint.End, inputRange, TextPatternRangeEndpoint.Start); }
                var text = range.GetText(160000).Trim();
                if (text.Length > 0) return SplitTranscript(text);
            }
            catch (InvalidOperationException) { }
            catch (ArgumentException) { }
        }
        var elements = Walk(conversation, 4000);
        var containers = elements.Where(e =>
        {
            var cl = Class(e);
            return cl.Contains("user-message") || cl.Contains("assistant-message") || cl.Contains("markdown") || cl.Contains("MessageContent") || cl.Contains("message-content");
        }).ToList();
        var blocks = new List<MessageBlock>(); var seen = new HashSet<string>();
        foreach (var node in containers)
        {
            var text = ReadText(node); if (string.IsNullOrWhiteSpace(text) || !seen.Add(text)) continue;
            if (blocks.Any(b => b.Text.Contains(text, StringComparison.Ordinal))) continue;
            var role = Class(node).Contains("user-message") ? "user" : "assistant";
            blocks.Add(new(Wire.Hash($"{blocks.Count}|{role}|{text}"), role, text));
        }
        if (blocks.Count == 0)
        {
            var text = ReadText(conversation);
            if (!string.IsNullOrWhiteSpace(text)) blocks.Add(new(Wire.Hash(text), "transcript", text));
        }
        return blocks.TakeLast(100).ToList();
    }
    private static List<MessagePart> ExtractArticleParts(AutomationElement article, AutomationElement? heading)
    {
        var parts = new List<MessagePart>(); var stack = new Stack<(AutomationElement Element, int Depth)>(); var readBlocks = new HashSet<string>();
        stack.Push((article, 0)); var count = 0; var excluded = heading?.GetRuntimeId(); var watch = Stopwatch.StartNew();
        while (stack.Count > 0 && count++ < 1200 && watch.ElapsedMilliseconds < 2500)
        {
            var (e, depth) = stack.Pop(); var cl = Class(e);
            if (excluded != null && e.GetRuntimeId().SequenceEqual(excluded) || cl.Contains("sr-only") || Type(e, ControlType.ToolBar) || Type(e, ControlType.StatusBar)) continue;
            var kind = PartKind(e);
            if (UiaContent.IsBlock(cl)) { AddUiaBlock(parts, e, readBlocks); continue; }
            if (Type(e, ControlType.Text) || Type(e, ControlType.Hyperlink))
            {
                var paragraph = UiaContent.Ancestor(e);
                if (paragraph != null) AddUiaBlock(parts, paragraph, readBlocks);
                else TranscriptParts.Add(parts, kind, Name(e));
                continue;
            }
            if (Type(e, ControlType.Button)) { if (kind != "body") TranscriptParts.Add(parts, kind, Name(e)); continue; }
            if (depth >= 32) continue;
            var children = new List<AutomationElement>();
            for (var child = Walker.GetFirstChild(e); child != null && children.Count < 1200; child = Walker.GetNextSibling(child)) children.Add(child);
            for (var i = children.Count - 1; i >= 0; i--) stack.Push((children[i], depth + 1));
        }
        return parts;
    }
    private static void AddUiaBlock(List<MessagePart> parts, AutomationElement node, HashSet<string> visited)
    {
        if (!visited.Add(string.Join('-', node.GetRuntimeId()))) return;
        TranscriptParts.AddBlocks(parts, PartKind(node), [UiaContent.Read(node)]);
    }
    private static (bool Paragraph, string Classes, bool Reasoning) PartContext(AutomationElement e)
    {
        var paragraph = false; var reasoning = false; var classes = new StringBuilder();
        var current = e;
        for (var i = 0; current != null && i < 7; i++, current = TreeWalker.RawViewWalker.GetParent(current))
        {
            var cl = Class(current); classes.Append(' ').Append(cl);
            paragraph |= cl.Contains("_Paragraph_") || cl.Contains("whitespace-pre-wrap") || cl.Contains("markdown");
            reasoning |= TranscriptParts.IsReasoningClass(cl);
        }
        return (paragraph, classes.ToString(), reasoning);
    }
    private static string PartKind(AutomationElement e)
    {
        var context = PartContext(e);
        return TranscriptParts.Classify(Name(e), Type(e, ControlType.Button), context.Paragraph, context.Classes, context.Reasoning);
    }
    private static List<MessageBlock> ExtractChatGpt(AutomationElement conversation, AutomationElement? editor)
    {
        // The control view flattens message articles. Traverse in document order,
        // using the real accessible role headings as boundaries instead of parents.
        var result = new List<MessageBlock>(); var parts = new List<MessagePart>(); var pendingAssistant = new List<MessagePart>();
        var readBlocks = new HashSet<string>();
        var role = ""; int[]? layoutId = null; var editorId = editor?.GetRuntimeId();
        void Flush()
        {
            var text = string.Join("\n", parts.Select(p => p.Text)).Trim();
            if (role.Length > 0 && text.Length > 0) result.Add(TranscriptParts.Block(Wire.Hash(result.Count + "|" + role), role, parts));
            parts.Clear();
        }
        var stack = new Stack<(AutomationElement Element, int Depth)>(); stack.Push((conversation, 0));
        var watch = Stopwatch.StartNew(); var count = 0;
        while (stack.Count > 0 && count++ < 5000 && watch.ElapsedMilliseconds < 3500)
        {
            var (e, depth) = stack.Pop();
            if (editorId != null && e.GetRuntimeId().SequenceEqual(editorId)) break;
            var cl = Class(e); var name = Name(e);
            if (Type(e, ControlType.Text) && IsTranscriptHeading(name) && cl.Contains("sr-only"))
            {
                Flush(); role = UserHeading(name) ? "user" : "assistant";
                if (role == "assistant" && pendingAssistant.Count > 0) { parts.AddRange(pendingAssistant); pendingAssistant.Clear(); }
                var parent = TreeWalker.RawViewWalker.GetParent(e);
                layoutId = parent != null && Class(parent).Contains("relative shrink-0") ? parent.GetRuntimeId() : null;
                continue;
            }
            // The hidden 'latest reply' heading starts the composer, not a message.
            if (Type(e, ControlType.Text) && cl.Contains("sr-only") && (e.Current.LocalizedControlType.Contains("标题") || e.Current.LocalizedControlType.Contains("heading", StringComparison.OrdinalIgnoreCase))) break;
            if (Type(e, ControlType.Button))
            {
                var kind = PartKind(e);
                if (kind != "body") TranscriptParts.Add(role == "user" ? pendingAssistant : parts, kind, name);
                else if (role.Length > 0 && PartContext(e).Paragraph)
                {
                    var paragraph = UiaContent.Ancestor(e);
                    if (paragraph != null) AddUiaBlock(parts, paragraph, readBlocks);
                    else TranscriptParts.Add(parts, "body", name);
                }
                continue;
            }
            if (Type(e, ControlType.ToolBar) || Type(e, ControlType.StatusBar) || cl.Contains("sr-only")) continue;
            if (role.Length > 0 && UiaContent.IsBlock(cl))
            {
                var kind = PartKind(e);
                AddUiaBlock(kind != "body" && role == "user" ? pendingAssistant : parts, e, readBlocks);
                continue;
            }
            if ((Type(e, ControlType.Text) || Type(e, ControlType.Hyperlink)) && role.Length > 0 && name.Length > 0)
            {
                var parent = TreeWalker.RawViewWalker.GetParent(e);
                var kind = PartKind(e);
                if (kind != "body") TranscriptParts.Add(role == "user" ? pendingAssistant : parts, kind, name);
                else if (layoutId == null || parent == null || !parent.GetRuntimeId().SequenceEqual(layoutId))
                {
                    var paragraph = UiaContent.Ancestor(e);
                    if (paragraph != null) AddUiaBlock(parts, paragraph, readBlocks);
                    else TranscriptParts.Add(parts, "body", name);
                }
                continue;
            }
            if (depth >= 32) continue;
            var children = new List<AutomationElement>();
            for (var child = Walker.GetFirstChild(e); child != null && children.Count < 2000; child = Walker.GetNextSibling(child)) children.Add(child);
            for (var i = children.Count - 1; i >= 0; i--) stack.Push((children[i], depth + 1));
        }
        Flush();
        if (pendingAssistant.Count > 0) result.Add(TranscriptParts.Block(Wire.Hash("pending|" + string.Join("|", pendingAssistant.Select(p => p.Text))), "assistant", pendingAssistant));
        return result.TakeLast(100).ToList();
    }
    private static List<MessageBlock> SplitTranscript(string text)
    {
        text = text.Replace("\uFFFC", "");
        var matches = Regex.Matches(text, @"(?m)^(你说：|ChatGPT\s*说：|You said:|ChatGPT said:)\s*$");
        if (matches.Count == 0) return [new(Wire.Hash(text), "transcript", text)];
        var result = new List<MessageBlock>();
        for (var i = 0; i < matches.Count; i++)
        {
            var start = matches[i].Index + matches[i].Length;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;
            var content = text[start..end].Trim();
            if (content.Length == 0) continue;
            var role = matches[i].Value.Contains("你说") || matches[i].Value.Contains("You said") ? "user" : "assistant";
            result.Add(new(Wire.Hash($"{i}|{role}|{content}"), role, content));
        }
        return result.TakeLast(100).ToList();
    }
    private static bool UserHeading(string name) => name.StartsWith("你说") || name.StartsWith("You said");
    private static bool IsTranscriptHeading(string name) => name is "你说：" or "ChatGPT 说：" or "You said:" or "ChatGPT said:" or "Claude responded:";
    private static List<MessageBlock> ExtractByRegion(Context c, AutomationElement? editor)
    {
        if (editor == null) return [];
        if (Provider == "DeepSeek" && c.Elements.Any(e => Name(e) == "探索未至之境")) return [];
        var input = editor.Current.BoundingRectangle;
        var doc = c.Document?.Current.BoundingRectangle ?? c.Window.Current.BoundingRectangle;
        var top = doc.Top + 105;
        var values = new List<MessagePart>();
        var stack = new Stack<(AutomationElement Element, int Depth)>(); stack.Push((c.Document ?? c.Window, 0));
        var n = 0;
        while (stack.Count > 0 && n++ < 5000)
        {
            var (e, depth) = stack.Pop();
            try
            {
                var rect = e.Current.BoundingRectangle;
                var type = e.Current.ControlType;
                if (type == ControlType.Menu || type == ControlType.MenuBar) continue;
                if ((type == ControlType.Text || type == ControlType.ListItem) && rect.Width > 0 && rect.Left >= input.Left - 55 && rect.Top >= top && rect.Bottom <= input.Top + 2 && !Class(e).Contains("sr-only"))
                {
                    var value = Name(e).Trim();
                    if (value.Length > 0 && value is not "已完成" and not "已停止" && !value.StartsWith("Can’t reach ") && !value.StartsWith("The provider didn’t respond") && !value.StartsWith("Arrow keys move the tile")) TranscriptParts.Add(values, PartKind(e), value);
                }
                if (depth < 32)
                {
                    var children = new List<AutomationElement>();
                    for (var child = Walker.GetFirstChild(e); child != null && children.Count < 2000; child = Walker.GetNextSibling(child)) children.Add(child);
                    for (var i = children.Count - 1; i >= 0; i--) stack.Push((children[i], depth + 1));
                }
            }
            catch (ElementNotAvailableException) { }
        }
        var text = string.Join("\n", values.Select(p => p.Text));
        return text.Length == 0 ? [] : [TranscriptParts.Block("loaded-transcript", "transcript", values)];
    }
    private static string ReadText(AutomationElement root)
    {
        if (root.TryGetCurrentPattern(TextPattern.Pattern, out var pattern))
        {
            var value = ((TextPattern)pattern).DocumentRange.GetText(160000).Trim();
            if (value.Length > 0) return value;
        }
        var text = Walk(root, 3500).Where(e => Type(e, ControlType.Text)).Select(Name).Where(n => !string.IsNullOrWhiteSpace(n));
        return string.Join("\n", text).Trim();
    }
    private static BridgeReply Read(string expectedKey = "")
    {
        try
        {
            if (Provider == "Claude" && VitroHUD.Probe.ClaudeConnection.IsConfigured)
            {
                try
                {
                    var source = new VitroHUD.Probe.ClaudeConnection().Read();
                    return expectedKey.Length == 0 || expectedKey == source.SourceKey ? new(true, source) : new(false, Error: "原会话连接已变化，请重新核对。");
                }
                catch { return new(false, Error: "Claude 后台连接未就绪，请返回绑定的原会话；重开客户端后需重新执行连接脚本。"); }
            }
            if (Provider == "ChatGPT" && VitroHUD.Probe.CodexPipe.ConfiguredConversation() is { Length: > 0 } selected)
            {
                try
                {
                    if (codexPipe == null || codexConversation != selected)
                    {
                        codexPipe?.Dispose(); codexPipe = null;
                        codexPipe = new(selected); codexConversation = selected;
                    }
                    var pipeSource = codexPipe.Read();
                    return expectedKey.Length == 0 || expectedKey == pipeSource.SourceKey ? new(true, pipeSource) : new(false, Error: "原会话连接已变化，请重新核对。");
                }
                catch { codexPipe?.Dispose(); codexPipe = null; return new(false, Error: "无法连接绑定的 Codex 会话，请先在原客户端打开这段会话；侧栏会继续尝试连接。"); }
            }
            if (backgroundBinding is { } bound && (expectedKey.Length == 0 || expectedKey == bound.Source.SourceKey))
            {
                using var attached = OpenBoundPage(bound);
                if (attached != null)
                {
                    var view = attached.ReadView(bound.Source.Model, Provider);
                    if (!view.State.GetProperty("stableIdentity").GetBoolean() || view.State.GetProperty("identity").GetString() != bound.Identity)
                    {
                        backgroundBinding = null;
                        return new(false, Error: "原客户端已切换会话，请重新连接");
                    }
                    var live = WithBackground(bound.Source, view.State, true) with
                    {
                        ClientMinimized = IsIconic((nint)bound.Source.WindowHandle),
                        ObservedAt = DateTimeOffset.UtcNow, Messages = view.Messages
                    };
                    backgroundBinding = bound with { Source = live };
                    return new(true, live);
                }
            }
            var c = Locate();
            if (c == null) return new(true, new() { Provider = Provider, Detail = "请打开官方客户端中的一段会话" });
            var snapshot = Snapshot(c);
            using var page = BackgroundPage.Connect(Provider, c.Title, c.Window.Current.ProcessId);
            if (page == null) return expectedKey.Length == 0 || expectedKey == snapshot.SourceKey ? new(true, snapshot) : new(false, Error: "原客户端已切换会话，请重新连接");
            var viewNow = page.ReadView(snapshot.Model, Provider);
            var connected = BackgroundSource(c, snapshot, page, viewNow.State) with { Messages = viewNow.Messages };
            if (expectedKey.Length > 0 && expectedKey != connected.SourceKey) return new(false, Error: "原客户端已切换会话，请重新连接");
            if (viewNow.State.GetProperty("stableIdentity").GetBoolean() && viewNow.State.GetProperty("canRead").GetBoolean())
                backgroundBinding = new(connected, c.Window.Current.ProcessId, page.TargetId, viewNow.State.GetProperty("identity").GetString()!);
            return new(true, connected);
        }
        catch (Exception ex) { return new(false, Error: SafeError(ex)); }
    }
    private static SourceSnapshot BackgroundSource(Context context, SourceSnapshot source, BackgroundPage page, JsonElement state)
    {
        var strong = state.GetProperty("stableIdentity").GetBoolean();
        var connected = WithBackground(source, state, strong);
        return connected with
        {
            SourceKey = strong ? Wire.Hash($"{Provider}|{context.Window.Current.ProcessId}|{source.WindowHandle}|page:{page.TargetId}|{state.GetProperty("identity").GetString()}") : source.SourceKey,
            BackgroundConnected = strong,
            Status = strong ? connected.Status : source.Status,
            Detail = strong ? connected.Detail : "已读取官方会话 · 后台操作身份尚未确认",
            Capabilities = strong ? connected.Capabilities : source.Capabilities
        };
    }
    private static bool SameWindow(PageBinding binding)
    {
        if (!IsWindow((nint)binding.Source.WindowHandle)) return false;
        GetWindowThreadProcessId((nint)binding.Source.WindowHandle, out var processId);
        return processId == binding.ProcessId;
    }
    private static BackgroundPage? OpenBoundPage(PageBinding binding)
    {
        if (!SameWindow(binding)) { backgroundBinding = null; return null; }
        var page = BackgroundPage.Connect(Provider, binding.Source.Title, binding.ProcessId, binding.TargetId);
        if (page == null) return null;
        var state = page.State(binding.Source.Model);
        if (state.GetProperty("stableIdentity").GetBoolean() && state.GetProperty("identity").GetString() == binding.Identity) return page;
        page.Dispose(); backgroundBinding = null; return null;
    }
    private static BridgeReply Handle(BridgeRequest request)
    {
        if (Provider == "Claude" && request.Op == "prepare-claude")
        {
            var context = Locate();
            if (context?.Document == null || !context.StableIdentity || !context.Document.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern)) return new(false, Error: "请先在 Claude 恢复并打开要连接的 Code 会话。");
            var session = ClaudeConversationId(((ValuePattern)pattern).Current.Value);
            try
            {
                var script = VitroHUD.Probe.ClaudeConnection.Prepare(request.Value, session, context.Window.Current.ProcessId, context.Window.Current.NativeWindowHandle);
                return new(true, Choices: [script]);
            }
            catch { return new(false, Error: "无法准备连接，请填写原会话的实际本机工作目录，并确认目录不是链接。"); }
        }
        if (Provider == "Claude" && request.Op == "disconnect-claude")
        {
            VitroHUD.Probe.ClaudeConnection.Disconnect(); return new(true);
        }
        if (Provider == "ChatGPT" && request.Op == "connect-thread")
        {
            var selected = VitroHUD.Probe.CodexPipe.ParseConversation(request.Value);
            if (selected.Length == 0) return new(false, Error: "请粘贴现有 Codex 会话链接或完整会话 ID。");
            try
            {
                var candidate = new VitroHUD.Probe.CodexPipe(selected);
                try
                {
                    var connected = candidate.Read();
                    VitroHUD.Probe.CodexPipe.Configure(selected);
                    codexPipe?.Dispose(); codexPipe = candidate; codexConversation = selected;
                    return new(true, connected);
                }
                catch { candidate.Dispose(); throw; }
            }
            catch { return new(false, Error: "未连接到选定会话，请先在原客户端打开该会话，再连接。"); }
        }
        if (Provider == "ChatGPT" && request.Op == "disconnect-thread")
        {
            codexPipe?.Dispose(); codexPipe = null; codexConversation = "";
            System.IO.File.Delete(VitroHUD.Probe.CodexPipe.ConnectionPath);
            return Read();
        }
        if (request.Op == "read") return Read(request.SourceKey);
        if (request.SourceKey.Length == 0) return new(false, Error: "请先连接目标会话");
        if (Provider == "Claude" && VitroHUD.Probe.ClaudeConnection.IsConfigured)
        {
            try { return new VitroHUD.Probe.ClaudeConnection().Handle(request); }
            catch { return new(false, Error: "Claude 后台连接已失效，请重新连接原会话。"); }
        }
        if (Provider == "ChatGPT" && VitroHUD.Probe.CodexPipe.ConfiguredConversation() is { Length: > 0 } selectedConversation)
            return codexPipe != null && selectedConversation == codexConversation ? codexPipe.Handle(request) : new(false, Error: "后台会话连接已变化，请重新核对。");
        if (backgroundBinding is { } bound && bound.Source.SourceKey == request.SourceKey)
        {
            using var attached = OpenBoundPage(bound);
            if (attached != null) return HandleBackground(bound.Source, attached, request,
                () => SameWindow(bound) && attached.State(bound.Source.Model).GetProperty("identity").GetString() == bound.Identity);
        }
        var c = Locate(request.SourceKey) ?? Locate();
        if (c == null) return new(false, Error: "原客户端已切换会话，操作已取消");
        using (var page = BackgroundPage.Connect(Provider, c.Title, c.Window.Current.ProcessId))
            if (page != null)
            {
                var original = Snapshot(c); var state = page.State(original.Model); var source = BackgroundSource(c, original, page, state);
                var identity = state.GetProperty("identity").GetString();
                if (state.GetProperty("stableIdentity").GetBoolean())
                {
                    if (source.SourceKey != request.SourceKey) return new(false, Error: "原客户端会话身份已变化，操作已取消");
                    var binding = new PageBinding(source, c.Window.Current.ProcessId, page.TargetId, identity!);
                    backgroundBinding = binding;
                    return HandleBackground(source, page, request, () => SameWindow(binding) && page.State(source.Model).GetProperty("identity").GetString() == identity);
                }
            }
        if (c.Key != request.SourceKey) return new(false, Error: "原客户端已切换会话，操作已取消");
        if (!c.StableIdentity) return new(false, Error: "尚未可靠确认当前会话身份，请在官方客户端操作；侧栏继续同步内容和模型。");
        var minimized = IsIconic(c.Window.Current.NativeWindowHandle);
        if (minimized)
        {
            var current = Snapshot(c).Capabilities;
            var allowed = request.Op == "send" ? current.CanSend : request.Op == "stop" ? current.CanStop : current.CanSelectModel;
            if (!allowed) return new(false, Error: "当前会话尚未确认后台操作能力，请先连接后台会话。");
        }
        if (request.Op == "send")
        {
            if (string.IsNullOrWhiteSpace(request.Text)) return new(false, Error: "请输入消息");
            var state = Snapshot(c);
            if (state.IsGenerating) return new(false, Error: "原客户端正在生成，请等待完成");
            if (state.Status == "client-error") return new(false, Error: state.Detail);
            var input = Editor(c); var button = SendButton(c);
            if (input == null || !input.TryGetCurrentPattern(ValuePattern.Pattern, out var value)) return new(false, Error: "当前客户端未提供可写输入框");
            object? action = null;
            if (Provider is not ("Claude" or "ChatGPT") && (button == null || !button.TryGetCurrentPattern(InvokePattern.Pattern, out action))) return new(false, Error: "未能可靠识别发送按钮");
            var vp = (ValuePattern)value;
            if (vp.Current.IsReadOnly) return new(false, Error: "客户端输入框当前只读");
            if (!string.IsNullOrWhiteSpace(DraftValue(input))) return new(false, Error: "原客户端已有草稿，请先处理原草稿");
            if (request.AllowForegroundInput && !minimized)
            {
                if (!ForegroundInput.Paste(input, c.Window.Current.NativeWindowHandle, request.OriginWindow, request.Text, out var inputError)) return new(false, Error: inputError);
            }
            else vp.SetValue(request.Text);
            var acceptWatch = Stopwatch.StartNew();
            while (acceptWatch.ElapsedMilliseconds < 4000)
            {
                Thread.Sleep(120);
                var refreshed = Locate(request.SourceKey);
                if (refreshed == null) return new(false, Error: "会话已变更；已取消提交，请检查原草稿");
                c = refreshed;
                if (NormalizeDraft(DraftValue(Editor(c))) == NormalizeDraft(request.Text) && Enabled(SendButton(c))) break;
            }
            // Recheck after editing; never submit if the source changed.
            c = Locate(request.SourceKey);
            if (c == null) return new(false, Error: "会话已变更；消息保留在原客户端草稿中");
            if (DraftValue(Editor(c)).Replace("\r\n", "\n").TrimEnd('\n') != request.Text.Replace("\r\n", "\n").TrimEnd('\n')) return new(false, Error: "原客户端草稿与待发送内容不同，已取消提交，请检查原草稿。");
            button = SendButton(c);
            if (!Enabled(button) || button?.TryGetCurrentPattern(InvokePattern.Pattern, out action) != true) return new(false, Error: "客户端未接受后台输入。可开启“发送时激活原客户端”，或在原客户端处理草稿。");
            ((InvokePattern)action!).Invoke();
            var receipt = Stopwatch.StartNew();
            while (receipt.ElapsedMilliseconds < 3000)
            {
                Thread.Sleep(80);
                var accepted = Locate(request.SourceKey);
                if (accepted == null) break;
                if (NormalizeDraft(DraftValue(Editor(accepted))).Length == 0) return new(true, Snapshot(accepted));
            }
            return new(false, Error: "提交结果待确认，请检查原客户端；不会自动重发。");
        }
        if (request.Op == "stop")
        {
            var button = StopButton(c);
            if (button?.TryGetCurrentPattern(InvokePattern.Pattern, out var action) != true) return new(false, Error: "当前没有可用的停止操作");
            ((InvokePattern)action).Invoke(); return new(true);
        }
        if (request.Op == "model-menu")
        {
            if (!OpenModelRoot(c)) return new(false, Error: "请在官方客户端调整模型");
            return MenuReply(c, "模型菜单已在官方客户端打开；可在原窗口继续选择");
        }
        if (request.Op == "choose-model")
        {
            if (request.MenuPath?.Count > 4) return new(false, Error: "模型菜单路径过深，请重新打开");
            // Switching back to the glass closes many native menus. Reopen and replay the confirmed path.
            if (!OpenModelRoot(c)) return new(false, Error: "模型菜单暂不可用");
            foreach (var step in request.MenuPath ?? []) if (!PickMenuChoice(c, step)) return new(false, Error: "模型菜单已变化，请重新打开");
            var before = MenuChoices(c).Select(Name).Distinct().ToList();
            if (!PickMenuChoice(c, request.Value)) return new(false, Error: "模型菜单已变化，请重新打开");
            var after = MenuChoices(c).Select(Name).Distinct().ToList();
            if (after.SequenceEqual(before))
            {
                if (ModelButton(c)?.TryGetCurrentPattern(InvokePattern.Pattern, out var close) == true) ((InvokePattern)close).Invoke();
                after = [];
            }
            return new(true, Choices: after);
        }
        return new(false, Error: "不支持的操作");
    }
    private static SourceSnapshot WithBackground(SourceSnapshot source, JsonElement state, bool stable)
    {
        var model = state.GetProperty("model").GetString();
        return source with
        {
            BackgroundConnected = true, Status = "connected-background", Detail = "已连接官方会话 · 后台模式",
            Title = state.GetProperty("title").GetString() ?? source.Title,
            Draft = state.GetProperty("draft").GetString() ?? "", IsGenerating = state.GetProperty("stop").GetBoolean(),
            Model = string.IsNullOrWhiteSpace(model) ? source.Model : model.Replace("选择模型，当前 ", "").Replace("，推理等级 ", " ").Replace("Model: ", ""),
            Capabilities = new(state.GetProperty("canRead").GetBoolean(), stable && state.GetProperty("canRead").GetBoolean() && state.GetProperty("editor").GetBoolean() && state.GetProperty("send").GetBoolean(), stable && state.GetProperty("stop").GetBoolean(), stable && state.GetProperty("modelEnabled").GetBoolean())
        };
    }
    private static BridgeReply HandleBackground(SourceSnapshot source, BackgroundPage page, BridgeRequest request, Func<bool> stillBound)
    {
        var before = page.State(source.Model);
        var identity = before.GetProperty("identity").GetString();
        bool StillBound() => stillBound();
        if (request.Op == "send")
        {
            if (string.IsNullOrWhiteSpace(request.Text)) return new(false, Error: "请输入消息");
            if (before.GetProperty("stop").GetBoolean()) return new(false, Error: "原客户端正在生成，请等待完成");
            if (!string.IsNullOrWhiteSpace(before.GetProperty("draft").GetString())) return new(false, Error: "原客户端已有草稿，请先处理原草稿");
            if (!StillBound()) return new(false, Error: "原客户端已切换会话，后台输入已取消");
            if (!page.InsertText(request.Text, identity!)) return new(false, Error: "原客户端未接受后台输入，或会话、草稿已变化；已取消提交，请检查原草稿。");
            var after = page.State(source.Model);
            var editReceipt = Stopwatch.StartNew();
            while (NormalizeDraft(after.GetProperty("draft").GetString() ?? "") != NormalizeDraft(request.Text) && editReceipt.ElapsedMilliseconds < 1200)
            {
                if (!StillBound()) return new(false, Error: "会话已变化，已取消提交，请检查原草稿");
                Thread.Sleep(80); after = page.State(source.Model);
            }
            if (!StillBound()) return new(false, Error: "会话已变化，消息保留在原客户端草稿中");
            if (after.GetProperty("stop").GetBoolean() || NormalizeDraft(after.GetProperty("draft").GetString() ?? "") != NormalizeDraft(request.Text)) return new(false, Error: "后台输入未核对一致，已取消提交，请检查原草稿");
            // One invocation only. Never retry an uncertain submission.
            if (!page.Click("send", source.Model, expectedIdentity: identity, expectedDraft: request.Text)) return new(false, Error: "原客户端发送按钮、会话或草稿已变化，提交已取消");
            var receipt = Stopwatch.StartNew();
            while (receipt.ElapsedMilliseconds < 1100)
            {
                Thread.Sleep(80);
                if (!StillBound()) break;
                var accepted = page.State(source.Model);
                if (accepted.GetProperty("editor").GetBoolean() && string.IsNullOrEmpty(accepted.GetProperty("draft").GetString()))
                    return new(true);
            }
            return new(false, Error: "提交结果待确认，请检查原客户端；消息不会自动重发。");
        }
        if (request.Op == "stop") return StillBound() && page.Click("stop", source.Model, expectedIdentity: identity) ? new(true) : new(false, Error: "后台停止按钮不可用");
        if (request.Op is "model-menu" or "choose-model")
        {
            if (!StillBound()) return new(false, Error: "原客户端已切换会话，模型操作已取消");
            if (request.MenuPath?.Count > 4) return new(false, Error: "模型菜单路径已失效，请重新打开");
            if (page.Choices().Count > 0 && request.Op == "choose-model") { page.Click("model", source.Model, expectedIdentity: identity); Thread.Sleep(80); }
            if (page.Choices().Count == 0) { if (!page.Click("model", source.Model, expectedIdentity: identity)) return new(false, Error: "后台模型入口不可用"); Thread.Sleep(120); }
            List<string>? leafChoices = null;
            if (request.Op == "choose-model")
            {
                foreach (var step in request.MenuPath ?? []) { if (!StillBound() || !page.Click("choice", source.Model, step, identity)) return new(false, Error: "模型菜单已变化，请重新打开"); Thread.Sleep(120); }
                leafChoices = page.Choices();
                if (!StillBound() || !page.Click("choice", source.Model, request.Value, identity)) return new(false, Error: "模型选项已变化，请重新打开");
                Thread.Sleep(120);
            }
            var choices = page.Choices();
            if (request.Op == "model-menu" || choices.Count > 0 && !choices.SequenceEqual(leafChoices ?? []))
                return new(choices.Count > 0, Choices: choices, Error: choices.Count > 0 ? "" : "此客户端菜单尚未支持后台读取，请在原客户端选择");
            var selection = Stopwatch.StartNew();
            while (selection.ElapsedMilliseconds < 1000)
            {
                if (!StillBound()) return new(false, Error: "原客户端已切换会话，模型选择未确认");
                var actual = page.State(source.Model);
                if (ModelChoiceMatches(actual.GetProperty("model").GetString() ?? "", request.Value))
                {
                    if (choices.Count > 0) page.Click("model", source.Model, expectedIdentity: identity);
                    return new(true);
                }
                Thread.Sleep(100);
            }
            return new(false, Error: "原客户端尚未确认模型选择，侧栏将显示实际回读的模型。");
        }
        return new(false, Error: "不支持的后台操作");
    }
    private static bool ModelChoiceMatches(string label, string choice)
    {
        choice = Regex.Replace(choice, @"\s+", " ").Trim();
        label = Regex.Replace(label, @"\s+", " ").Trim();
        return choice.Length > 0 && Regex.IsMatch(label, @"(?:^|[\s·,，])" + Regex.Escape(choice) + @"(?=$|[\s·,，])", RegexOptions.IgnoreCase);
    }
    private static string NormalizeDraft(string text) => text.Replace("\r\n", "\n").TrimEnd('\n');
    private static void Inspect(bool editorOnly = false)
    {
        var c = Locate();
        if (c == null) { Console.WriteLine("{\"status\":\"no-window\"}"); return; }
        var root = editorOnly ? Editor(c) : c.Conversation ?? c.Document ?? c.Window;
        if (root == null) { Console.WriteLine("{\"status\":\"no-editor\"}"); return; }
        var nodes = Walk(root, editorOnly ? 80 : 250, rawView: editorOnly).Select(e => new { Type = e.Current.ControlType.ProgrammaticName, Kind = e.Current.LocalizedControlType, Heading = IsTranscriptHeading(Name(e).Trim()), Class = Class(e)[..Math.Min(170, Class(e).Length)], ParentClass = TreeWalker.RawViewWalker.GetParent(e) is { } parent ? Class(parent)[..Math.Min(170, Class(parent).Length)] : "", Placeholder = Name(e).Trim() == Name(root).Trim(), Content = e.Current.IsContentElement, ReadOnly = editorOnly && Type(e, ControlType.Text) ? TextReadOnly(root, e) : null, Name = Type(e, ControlType.Text) ? $"[text:{Name(e).Length}]" : Name(e)[..Math.Min(80, Name(e).Length)] });
        Console.WriteLine(JsonSerializer.Serialize(nodes, Wire.Json));
    }
    private static void InspectMetadata()
    {
        var c = Locate();
        if (c == null) { Console.WriteLine("[]"); return; }
        var nodes = Walk(c.Conversation ?? c.Document ?? c.Window, 4000)
            .Where(e => (Type(e, ControlType.Text) || Type(e, ControlType.Button)) && TranscriptParts.IsStatusLabel(Name(e)))
            .Take(30).Select(e => { var context = PartContext(e); return new { Type = e.Current.ControlType.ProgrammaticName, Kind = PartKind(e), context.Paragraph, context.Reasoning, Classes = context.Classes[..Math.Min(900, context.Classes.Length)] }; }).ToList();
        Console.WriteLine(JsonSerializer.Serialize(nodes, Wire.Json));
    }
    private static int SelfTest()
    {
        if (Wire.Hash("a") != Wire.Hash("a") || Wire.Hash("a") == Wire.Hash("b")) return 1;
        var req = JsonSerializer.Deserialize<BridgeRequest>("{\"op\":\"send\",\"sourceKey\":\"x\",\"text\":\"测试\\n第二行\"}", Wire.Json)!;
        if (req.Text != "测试\n第二行") return 2;
        if (IsComposer("DeepSeek", "搜索会话名称", "GAkraW_searchInput")) return 3;
        if (!IsComposer("DeepSeek", "描述你想要构建的内容, / 调用指令, @ 文件或对话", "GRgSLq_input")) return 4;
        if (!IsComposer("Claude", "Prompt", "")) return 5;
        if (IsComposer("ChatGPT", "搜索聊天", "ProseMirror")) return 6;
        var transcript = SplitTranscript("你说：\n你好\nChatGPT 说：\n你好\n你说：\n你好");
        if (transcript.Count != 3 || transcript[0].Role != "user" || transcript[1].Role != "assistant" || transcript[2].Text != "你好") return 7;
        if (ClaudeConversationId("app://localhost/epitaxy?coldLaunch=1").Length != 0 || ClaudeConversationId("app://localhost/epitaxy/local_11111111-2222-4333-8444-555555555555") != "local_11111111-2222-4333-8444-555555555555") return 8;
        var menuRequest = JsonSerializer.Deserialize<BridgeRequest>("{\"op\":\"choose-model\",\"menuPath\":[\"推理等级 Max\"],\"value\":\"High\"}", Wire.Json)!;
        if (menuRequest.MenuPath?.Single() != "推理等级 Max" || menuRequest.Value != "High") return 9;
        if (!IsEmptyPlaceholder("和 Codex 一起工作\n", "和 Codex 一起工作", true, true)
            || IsEmptyPlaceholder("和 Codex 一起工作", "和 Codex 一起工作", false, true)
            || IsEmptyPlaceholder("和 Codex 一起工作\n真实草稿", "和 Codex 一起工作", true, false)) return 10;
        if (TranscriptParts.Classify("用时 3秒", true, false, "text-size-chat text-secondary", false) != "timing"
            || TranscriptParts.Classify("已思考 13s", true, false, "text-secondary", false) != "timing"
            || TranscriptParts.Classify("用时 3秒", false, true, "_Paragraph_xjxw3_2", false) != "body"
            || TranscriptParts.Classify("思考中", true, false, "text-secondary", false) != "reasoning-status"
            || TranscriptParts.Classify("这段正文讨论思考和用时", false, true, "_Paragraph_xjxw3_2", false) != "body"
            || TranscriptParts.Classify("04:55", false, false, "agentglass-timestamp", false) != "timing"
            || TranscriptParts.Classify("04:55", false, true, "agentglass-timestamp", false) != "body"
            || TranscriptParts.Classify("实际展开的思考内容", false, true, "reasoning-content _Paragraph_xjxw3_2", true) != "reasoning") return 11;
        var typed = TranscriptParts.Block("x", "assistant", [new("timing", "用时 3秒"), new("body", "回答")]);
        var decoded = JsonSerializer.Deserialize<MessageBlock>(JsonSerializer.Serialize(typed, Wire.Json), Wire.Json)!;
        if (decoded.Parts?.Count != 2 || decoded.Parts[0].Kind != "timing" || decoded.Parts[1].Text != "回答") return 12;
        if (JsonSerializer.Deserialize<MessageBlock>("{\"id\":\"x\",\"role\":\"assistant\",\"text\":\"旧格式正文\"}", Wire.Json)?.Parts != null) return 13;
        var drafts = new DraftStore();
        drafts.Put("Claude", "", "连接前写下的草稿");
        if (drafts.Bind("Claude", "", "session-a", "连接前写下的草稿") != "连接前写下的草稿") return 14;
        drafts.Put("DeepSeek", "session-b", "另一个会话的草稿");
        if (!drafts.Acknowledge("Claude", "session-a", "连接前写下的草稿") || drafts.Get("Claude", "session-a") != "" || drafts.Get("DeepSeek", "session-b") != "另一个会话的草稿") return 15;
        drafts.Put("Claude", "session-a", "新写的草稿");
        if (drafts.Acknowledge("Claude", "session-a", "旧请求") || drafts.Get("Claude", "session-a") != "新写的草稿") return 16;
        drafts.Put("Claude", "session-c", "已有目标草稿");
        if (drafts.Bind("Claude", "", "session-c", "未绑定草稿") != "已有目标草稿" || drafts.Get("Claude", "") != "未绑定草稿") return 17;
        var structured = new List<MessagePart>();
        TranscriptParts.AddBlocks(structured, "body", [new("paragraph", "Hello world !", [new("Hello "), new("world", Bold: true), new(" !")])]);
        TranscriptParts.AddBlocks(structured, "body", [new("code", "    pass\n\n", Language: "python")]);
        var formatted = JsonSerializer.Deserialize<MessageBlock>(JsonSerializer.Serialize(TranscriptParts.Block("formatted", "assistant", structured), Wire.Json), Wire.Json)!;
        if (formatted.Parts?[0].Blocks?.Count != 2 || formatted.Parts[0].Blocks![0].Inlines?[1].Bold != true || formatted.Parts[0].Blocks![1].Text != "    pass\n\n") return 18;
        var whitespace = new List<MessagePart>();
        TranscriptParts.AddBlocks(whitespace, "body", [new("code", "  \n\n")]);
        if (TranscriptParts.Block("whitespace", "assistant", whitespace).Text != "  \n\n") return 19;
        var lists = new List<MessagePart>();
        TranscriptParts.AddBlocks(lists, "body", [new("list-item", "parent", Level: 1, Marker: "•"), new("list-item", "child", Level: 2, Marker: "•")]);
        TranscriptParts.AddBlocks(lists, "body", [new("list-item", "next", Level: 1, Marker: "•")]);
        if (TranscriptParts.Block("lists", "assistant", lists).Text != "• parent\n  • child\n• next") return 22;
        using var metadataSample = JsonDocument.Parse("""
            [{"id":"metadata","role":"assistant","segments":[{"text":"用时 2秒","control":true,"paragraph":false,"classes":"","reasoning":false,"blocks":[]},{"text":"回答","control":false,"paragraph":true,"classes":"","reasoning":false,"blocks":[{"kind":"paragraph","text":"回答"}]}]}]
            """);
        var metadataParts = BackgroundPage.ParseMessages(metadataSample.RootElement).Single().Parts;
        if (metadataParts?.Count != 2 || metadataParts[0].Kind != "timing" || metadataParts[0].Text != "用时 2秒" || metadataParts[1].Kind != "body") return 23;
        const string testThread = "11111111-2222-4333-8444-555555555555";
        if (VitroHUD.Probe.CodexPipe.ParseConversation("codex://threads/" + testThread) != testThread || VitroHUD.Probe.CodexPipe.ParseConversation("https://example.org/" + testThread) != "" || VitroHUD.Probe.CodexPipe.ParseConversation("codex://threads/" + testThread + "?other=1") != "") return 24;
        using var pipeFixture = JsonDocument.Parse("""
            {"turnHistory":{"kind":"canonical","history":{"entitiesByKey":{"turn:b":{"turnId":"b","items":[{"type":"userMessage","id":"u2","content":[{"type":"text","text":"second"}]},{"type":"agentMessage","id":"a2","text":"**answer**"}]},"turn:a":{"turnId":"a","items":[{"type":"userMessage","id":"u1","content":[{"type":"text","text":"first"}]},{"type":"commandExecution","id":"tool","text":"not a reply"},{"type":"agentMessage","id":"a1","text":"answer"}]}},"islands":[{"entries":[{"key":"turn:a"},{"key":"turn:b"},{"key":"turn:b"}]}]}}}
            """);
        var pipeMessages = VitroHUD.Probe.CodexPipe.ExtractMessages(pipeFixture.RootElement);
        if (pipeMessages.Count != 4 || pipeMessages[0].Text != "first" || pipeMessages[2].Text != "second" || pipeMessages[3].Parts?[0].Blocks?[0].Inlines?[0].Bold != true) return 25;
        var markdown = VitroHUD.Probe.MarkdownBlocks.Parse("## 标题\n\n```python\n  a = 1\n\n```\n\n- **粗体** 和 `代码`", true);
        if (markdown.Count != 3 || markdown[0].Kind != "heading" || markdown[1].Text != "  a = 1\n" || markdown[2].Kind != "list-item" || markdown[2].Inlines?[0].Bold != true) return 26;
        var tableMarkdown = VitroHUD.Probe.MarkdownBlocks.Parse("| *列* | 值 |\n| --- | --- |\n| a | b |", true);
        if (tableMarkdown.Count != 2 || tableMarkdown[0].Kind != "table-row" || tableMarkdown[0].Inlines?[0].Italic != true || tableMarkdown[0].Text != "列\t值") return 26;
        var displayContract = VitroHUD.Probe.MarkdownBlocks.Parse("**粗体** 和 `代码`\n\n## *标题*\n\n- [链接](https://example.org)\n\n> 引用 **强调**", true);
        if (displayContract.Count != 4 || displayContract.Any(b => b.Inlines == null || string.Concat(b.Inlines.Select(i => i.Text)) != b.Text) || displayContract[0].Text != "粗体 和 代码" || displayContract[2].Text != "链接") return 28;
        using var claudeFixture = JsonDocument.Parse("""
            {"entries":[{"id":"u1","role":"user","parts":[{"kind":"body","text":"repeat"}]},{"id":"a1","group":"a","role":"assistant","parts":[{"kind":"body","text":"**first**"}]},{"id":"a2","group":"a","role":"assistant","parts":[{"kind":"body","text":"```python\n  value = 1\n```"}]},{"id":"u2","role":"user","parts":[{"kind":"body","text":"repeat"}]},{"id":"u2","role":"user","parts":[{"kind":"body","text":"duplicate hidden"}]},{"id":"tool","role":"tool","parts":[{"kind":"body","text":"hidden"}]}]}
            """);
        var claudeMessages = VitroHUD.Probe.ClaudeConnection.ExtractMessages(claudeFixture.RootElement);
        if (claudeMessages.Count != 3 || claudeMessages[0].Text != "repeat" || claudeMessages[2].Text != "repeat" || claudeMessages[1].Parts?[0].Blocks?[0].Inlines?[0].Bold != true || claudeMessages[1].Parts?[1].Blocks?[0].Text != "  value = 1") return 27;
        var migrationRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "VitroHUD-migration-" + Guid.NewGuid().ToString("N"));
        var oldData = System.IO.Path.Combine(migrationRoot, "AgentGlass"); var newData = System.IO.Path.Combine(migrationRoot, "VitroHUD");
        try
        {
            System.IO.Directory.CreateDirectory(oldData); System.IO.Directory.CreateDirectory(newData);
            System.IO.File.WriteAllText(System.IO.Path.Combine(oldData, "settings.json"), "{\"provider\":\"Claude\"}");
            System.IO.File.WriteAllText(System.IO.Path.Combine(newData, "settings.json"), "{\"provider\":\"ChatGPT\"}");
            System.IO.File.WriteAllText(System.IO.Path.Combine(oldData, "chatgpt-connection.json"), "{\"conversationId\":\"fixture\"}");
            System.IO.File.WriteAllText(System.IO.Path.Combine(oldData, "claude-connection.json"), "not valid json");
            AppPaths.Migrate(migrationRoot);
            if (!System.IO.File.ReadAllText(System.IO.Path.Combine(newData, "settings.json")).Contains("ChatGPT") || !System.IO.File.Exists(System.IO.Path.Combine(newData, "chatgpt-connection.json")) || System.IO.File.Exists(System.IO.Path.Combine(newData, "claude-connection.json"))) return 29;
            System.IO.File.Delete(System.IO.Path.Combine(newData, "chatgpt-connection.json")); AppPaths.Migrate(migrationRoot);
            if (System.IO.File.Exists(System.IO.Path.Combine(newData, "chatgpt-connection.json")) || !System.IO.File.Exists(System.IO.Path.Combine(oldData, "chatgpt-connection.json"))) return 29;
        }
        finally
        {
            foreach (var directory in new[] { oldData, newData })
            {
                foreach (var name in new[] { "settings.json", "chatgpt-connection.json", "claude-connection.json", ".agentglass-migrated-v1" }) System.IO.File.Delete(System.IO.Path.Combine(directory, name));
                if (System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory);
            }
            if (System.IO.Directory.Exists(migrationRoot)) System.IO.Directory.Delete(migrationRoot);
        }
        if (!ModelChoiceMatches("GPT-6.1 Terra High", "High") || !ModelChoiceMatches("GPT-6.1 Terra High", "GPT-6.1 Terra") || ModelChoiceMatches("GPT-6.1 Terra High", "GPT-6")) return 20;
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        try
        {
            listener.Start(); var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            if (!LocalEndpoint.OwnedBy(port, Environment.ProcessId) || LocalEndpoint.OwnedBy(port, -1)) return 21;
        }
        finally { listener.Stop(); }
        Console.WriteLine("{\"passed\":true,\"checks\":[\"wire-roundtrip\",\"source-identity\",\"search-field-exclusion\",\"provider-composer-fixtures\",\"repeated-message-order\",\"claude-canonical-session-guard\",\"model-menu-path-roundtrip\",\"placeholder-vs-real-draft\",\"body-vs-client-metadata\",\"typed-parts-roundtrip-and-legacy\",\"unbound-draft-migration\",\"inactive-submit-receipt\",\"newer-draft-protection\",\"target-draft-conflict\",\"structured-inline-and-code-roundtrip\",\"whitespace-code-preservation\",\"nested-list-copy\",\"empty-block-metadata\",\"model-selection-readback\",\"loopback-endpoint-owner\",\"explicit-thread-link-guard\",\"canonical-ipc-message-order\",\"ipc-markdown-and-code\",\"claude-message-groups-and-code\",\"markdown-renderer-text-contract\",\"legacy-config-migration\"]}"); return 0;
    }
}
