using System.Windows.Automation;
using System.Text.RegularExpressions;
using VitroHUD;

// Read a semantic paragraph once instead of inserting newlines between its
// accessible inline fragments. Missing formatting remains ordinary text.
internal static class UiaContent
{
    internal static AutomationElement? Ancestor(AutomationElement node)
    {
        for (var depth = 0; node != null && depth < 9; depth++, node = TreeWalker.RawViewWalker.GetParent(node))
            if (IsBlock(node.Current.ClassName ?? "") || node.Current.ControlType == ControlType.ListItem) return node;
        return null;
    }
    internal static bool IsBlock(string classes) => classes.Contains("paragraph", StringComparison.OrdinalIgnoreCase)
        || classes.Contains("whitespace-pre-wrap", StringComparison.OrdinalIgnoreCase)
        || classes.Contains("code-block", StringComparison.OrdinalIgnoreCase)
        || classes.Contains("codeblock", StringComparison.OrdinalIgnoreCase)
        || classes.Contains("listitem", StringComparison.OrdinalIgnoreCase)
        || classes.Contains("list-item", StringComparison.OrdinalIgnoreCase)
        || classes.Contains("_Heading_", StringComparison.OrdinalIgnoreCase)
        || classes.Contains("preformatted", StringComparison.OrdinalIgnoreCase);

    internal static FormattedBlock Read(AutomationElement element)
    {
        var classes = element.Current.ClassName ?? "";
        var code = classes.Contains("code-block", StringComparison.OrdinalIgnoreCase)
            || classes.Contains("codeblock", StringComparison.OrdinalIgnoreCase)
            || classes.Contains("preformatted", StringComparison.OrdinalIgnoreCase);
        var listItem = element.Current.ControlType == ControlType.ListItem || classes.Contains("listitem", StringComparison.OrdinalIgnoreCase) || classes.Contains("list-item", StringComparison.OrdinalIgnoreCase);
        var blockKind = code ? "code" : listItem ? "list-item" : classes.Contains("_Heading_") ? "heading" : "paragraph";
        var level = 0;
        if (listItem)
            for (var parent = TreeWalker.RawViewWalker.GetParent(element); parent != null && level < 8; parent = TreeWalker.RawViewWalker.GetParent(parent))
            {
                if (parent.Current.ControlType == ControlType.List) level++;
                if ((parent.Current.ClassName ?? "").Contains("thread-scroll-container")) break;
            }
        FormattedBlock Make(string text, List<FormattedInline>? inlines)
        {
            string? marker = null;
            if (listItem)
            {
                var prefix = Regex.Match(text, @"^(\d+[.)]|[•●▪])\s+");
                if (prefix.Success)
                {
                    marker = prefix.Groups[1].Value; text = text[prefix.Length..];
                    var remove = prefix.Length;
                    inlines = inlines?.Select(run => { var length = Math.Min(remove, run.Text.Length); remove -= length; return run with { Text = run.Text[length..] }; }).Where(run => run.Text.Length > 0).ToList();
                }
            }
            return new(blockKind, text, code ? null : inlines, Math.Max(listItem ? 1 : 0, level), Marker: marker);
        }
        string? rangeText = null; var rangeBold = false; var rangeItalic = false; List<FormattedInline>? formats = null;
        if (element.TryGetCurrentPattern(TextPattern.Pattern, out var raw))
        {
            var range = ((TextPattern)raw).DocumentRange;
            var text = range.GetText(160000).Replace("\r\n", "\n");
            if (text.Length > 0)
            {
                rangeText = text;
                rangeBold = range.GetAttributeValue(TextPattern.FontWeightAttribute) is int weight && weight >= 600;
                rangeItalic = range.GetAttributeValue(TextPattern.IsItalicAttribute) is true;
                if (!code) formats = ReadFormats(range, text);
            }
        }
        var runs = new List<FormattedInline>();
        var stack = new Stack<(AutomationElement Element, int Depth, bool Bold, bool Italic, bool InlineCode)>();
        stack.Push((element, 0, rangeBold, rangeItalic, false)); var count = 0;
        while (stack.Count > 0 && count++ < 800)
        {
            var (node, depth, bold, italic, inlineCode) = stack.Pop();
            var type = node.Current.ControlType;
            var cl = node.Current.ClassName ?? "";
            if (cl.Contains("sr-only") || type == ControlType.ToolBar || code && type == ControlType.Button) continue;
            bold |= cl.Contains("font-bold") || cl.Contains("font-semibold");
            italic |= cl.Contains("italic");
            inlineCode |= cl.Contains("inline-code") || cl.Contains("InlineCode");
            if (type == ControlType.Text || type == ControlType.Hyperlink || type == ControlType.Button)
            {
                var text = node.Current.Name ?? "";
                if (text.Length > 0)
                {
                    string? link = null;
                    if (node.TryGetCurrentPattern(ValuePattern.Pattern, out var value)
                        && Uri.TryCreate(((ValuePattern)value).Current.Value, UriKind.Absolute, out var uri)
                        && uri.Scheme is "http" or "https") link = uri.AbsoluteUri;
                    runs.Add(new(text, bold, italic, inlineCode, link));
                    continue; // A named fragment already represents its descendants.
                }
                // Chromium also uses unnamed Text nodes as inline formatting wrappers.
                // Descend through them so their named children inherit the style.
            }
            if (depth >= 32) continue;
            var children = new List<AutomationElement>();
            for (var child = TreeWalker.RawViewWalker.GetFirstChild(node); child != null && children.Count < 800; child = TreeWalker.RawViewWalker.GetNextSibling(child)) children.Add(child);
            for (var i = children.Count - 1; i >= 0; i--) stack.Push((children[i], depth + 1, bold, italic, inlineCode));
        }
        var content = string.Concat(runs.Select(r => r.Text));
        if (!code && rangeText == content && formats != null && !runs.Any(r => r.Bold || r.Italic || r.Code || r.Link != null))
            runs = formats;
        // Chromium sometimes exposes only a trailing range, while in other cases
        // the bounded raw walk is incomplete. Use the range only when it contains
        // all raw text, preserving its original whitespace and complete content.
        if (rangeText != null && rangeText != content && !rangeText.Contains('\uFFFC')
            && (content.Length == 0 || Regex.Replace(rangeText, @"\s+", "").Contains(Regex.Replace(content, @"\s+", ""), StringComparison.Ordinal)))
            return Make(rangeText, formats ?? [new(rangeText, rangeBold, rangeItalic)]);
        if (content.Length == 0) content = element.Current.Name ?? "";
        return Make(content, runs.Count > 0 ? runs : null);
    }
    private static List<FormattedInline>? ReadFormats(System.Windows.Automation.Text.TextPatternRange range, string expected)
    {
        var result = new List<FormattedInline>(); var span = range.Clone();
        span.MoveEndpointByRange(System.Windows.Automation.Text.TextPatternRangeEndpoint.End, span, System.Windows.Automation.Text.TextPatternRangeEndpoint.Start);
        for (var count = 0; count < 256; count++)
        {
            if (span.MoveEndpointByUnit(System.Windows.Automation.Text.TextPatternRangeEndpoint.End, System.Windows.Automation.Text.TextUnit.Format, 1) == 0) break;
            if (span.CompareEndpoints(System.Windows.Automation.Text.TextPatternRangeEndpoint.End, range, System.Windows.Automation.Text.TextPatternRangeEndpoint.End) > 0)
                span.MoveEndpointByRange(System.Windows.Automation.Text.TextPatternRangeEndpoint.End, range, System.Windows.Automation.Text.TextPatternRangeEndpoint.End);
            var text = span.GetText(160000).Replace("\r\n", "\n");
            if (text.Length == 0) break;
            var bold = span.GetAttributeValue(TextPattern.FontWeightAttribute) is int weight && weight >= 600;
            var italic = span.GetAttributeValue(TextPattern.IsItalicAttribute) is true;
            var font = span.GetAttributeValue(TextPattern.FontNameAttribute) as string ?? "";
            var code = font.Contains("mono", StringComparison.OrdinalIgnoreCase) || font.Contains("Consolas") || font.Contains("Menlo");
            result.Add(new(text, bold, italic, code));
            if (span.CompareEndpoints(System.Windows.Automation.Text.TextPatternRangeEndpoint.End, range, System.Windows.Automation.Text.TextPatternRangeEndpoint.End) >= 0) break;
            span.MoveEndpointByRange(System.Windows.Automation.Text.TextPatternRangeEndpoint.Start, span, System.Windows.Automation.Text.TextPatternRangeEndpoint.End);
        }
        return string.Concat(result.Select(r => r.Text)) == expected ? result : null;
    }
}
