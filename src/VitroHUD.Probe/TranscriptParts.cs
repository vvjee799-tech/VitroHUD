using System.Text.RegularExpressions;
using VitroHUD;

internal static partial class TranscriptParts
{
    [GeneratedRegex(@"^(?:(?:用时|已处理|已思考|思考了?|耗时)\s*\d[\d\s.smh小时分钟秒毫秒]*|(?:Thought|Thinking|Worked|Working|Reasoned)\s+for\s+\d[\w\s.]*)(?:[。.]|…)?$", RegexOptions.IgnoreCase)]
    private static partial Regex Duration();
    [GeneratedRegex(@"^(?:思考|推理|正在思考|思考中|深度思考|思考过程|推理过程|Thinking|Reasoning|Thoughts)(?:[.。…]{0,3})$", RegexOptions.IgnoreCase)]
    private static partial Regex ThinkingLabel();
    [GeneratedRegex(@"(?:^|[\s_/-])(?:reasoning|thinking|thoughts?)(?:[\s_/-]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex ThinkingClass();
    [GeneratedRegex(@"^(?:运行了命令|编辑了文件|编辑了多个文件|已查看 \d+ 张图像|已使用 |已搜索网页|Searched |Ran |Edited )")]
    private static partial Regex ActivityLabel();

    internal static bool IsReasoningClass(string classes) => ThinkingClass().IsMatch(classes);
    internal static bool IsStatusLabel(string text) => Duration().IsMatch(text.Trim()) || ThinkingLabel().IsMatch(text.Trim());
    internal static string Classify(string text, bool isControl, bool inParagraph, string classes, bool inReasoning)
    {
        // Text inside an actual message paragraph keeps its meaning even when it
        // happens to say "用时 3秒". UI status labels need structural evidence.
        if (inReasoning && !ThinkingLabel().IsMatch(text.Trim())) return "reasoning";
        if (inParagraph) return "body";
        if (classes.Contains("agentglass-timestamp")) return "timing";
        var uiStyle = classes.Contains("text-secondary") || classes.Contains("text-muted") || classes.Contains("tabular-nums") || classes.Contains("group/morph") || classes.Contains("relative overflow-visible py-0");
        if (isControl || uiStyle)
        {
            if (Duration().IsMatch(text.Trim())) return "timing";
            if (ThinkingLabel().IsMatch(text.Trim())) return "reasoning-status";
            if (ActivityLabel().IsMatch(text.Trim())) return "activity";
            if (text.StartsWith("Compacted session") || text == "上下文已自动压缩") return "context";
        }
        return inReasoning ? "reasoning" : "body";
    }
    internal static void Add(List<MessagePart> parts, string kind, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (parts.Count > 0 && parts[^1].Kind == kind && parts[^1].Blocks == null)
        {
            if (kind != "body" && parts[^1].Text == text) return;
            parts[^1] = parts[^1] with { Text = parts[^1].Text + "\n" + text };
        }
        else parts.Add(new(kind, text));
    }
    internal static void AddBlocks(List<MessagePart> parts, string kind, List<FormattedBlock> blocks)
    {
        blocks = blocks.Where(b => b.Text.Length > 0 && (b.Kind == "code" || !string.IsNullOrWhiteSpace(b.Text))).ToList();
        if (blocks.Count == 0) return;
        var text = PlainText(blocks);
        if (parts.Count > 0 && parts[^1].Kind == kind && parts[^1].Blocks != null)
        {
            var merged = new List<FormattedBlock>(parts[^1].Blocks!); merged.AddRange(blocks);
            parts[^1] = parts[^1] with { Text = PlainText(merged), Blocks = merged };
        }
        else parts.Add(new(kind, text, blocks));
    }
    private static string PlainText(List<FormattedBlock> blocks)
    {
        var result = new System.Text.StringBuilder();
        for (var i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            if (i > 0) result.Append(block.Kind == blocks[i - 1].Kind && block.Kind is "list-item" or "table-row" ? "\n" : "\n\n");
            if (block.Kind == "list-item") result.Append(' ', Math.Clamp(block.Level - 1, 0, 12) * 2).Append(block.Marker ?? "•").Append(' ');
            result.Append(block.Text);
        }
        return result.ToString();
    }
    internal static MessageBlock Block(string id, string role, List<MessagePart> parts) =>
        new(id, role, string.Join("\n", parts.Select(p => p.Text)), [.. parts]);
}
