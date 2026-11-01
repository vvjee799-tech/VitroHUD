using System.Text.RegularExpressions;
using VitroHUD;

namespace VitroHUD.Probe;

internal static partial class MarkdownBlocks
{
    [GeneratedRegex(@"^(?<indent>\s*)(?<marker>[-+*]|\d+[.)])\s+(?<text>.+)$", RegexOptions.None, 100)] private static partial Regex ListLine();
    [GeneratedRegex(@"(`[^`\n]+`|\*\*[^*\n]+\*\*|__[^_\n]+__|(?<!\*)\*[^*\n]+\*(?!\*)|(?<!_)_[^_\n]+_(?!_)|\[[^\]\n]+\]\(https?://[^\s)]+\))", RegexOptions.None, 100)] private static partial Regex Inline();
    [GeneratedRegex(@"^\s*\|?\s*:?-{3,}:?\s*(?:\|\s*:?-{3,}:?\s*)+\|?\s*$", RegexOptions.None, 100)] private static partial Regex TableRule();
    internal static List<FormattedBlock> Parse(string text, bool markdown)
    {
        try { return ParseCore(text, markdown); }
        catch (RegexMatchTimeoutException) { return [new("paragraph", text, [new(text)])]; }
    }
    private static List<FormattedBlock> ParseCore(string text, bool markdown)
    {
        if (!markdown) return [new("paragraph", text, [new(text)])];
        var result = new List<FormattedBlock>();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var paragraph = new List<string>();
        void Flush() { if (paragraph.Count == 0) return; var value = string.Join("\n", paragraph); result.Add(Formatted("paragraph", value)); paragraph.Clear(); }
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index]; var trimmed = line.TrimStart();
            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
            {
                Flush(); var fence = trimmed[..3]; var language = trimmed[3..].Trim(); var code = new List<string>();
                while (++index < lines.Length && !lines[index].TrimStart().StartsWith(fence)) code.Add(lines[index]);
                result.Add(new("code", string.Join("\n", code), Language: language)); continue;
            }
            if (string.IsNullOrWhiteSpace(line)) { Flush(); continue; }
            if (line.Contains('|') && index + 1 < lines.Length && TableRule().IsMatch(lines[index + 1]))
            {
                Flush();
                void Row(string value) { value = string.Join("\t", value.Trim().Trim('|').Split('|').Select(c => c.Trim())); result.Add(Formatted("table-row", value)); }
                Row(line); index++;
                while (index + 1 < lines.Length && lines[index + 1].Contains('|') && !string.IsNullOrWhiteSpace(lines[index + 1])) Row(lines[++index]);
                continue;
            }
            var heading = trimmed.TakeWhile(c => c == '#').Count();
            if (heading is > 0 and <= 6 && trimmed.Length > heading && trimmed[heading] == ' ')
            { Flush(); var value = trimmed[(heading + 1)..]; result.Add(Formatted("heading", value, heading)); continue; }
            var list = ListLine().Match(line);
            if (list.Success) { Flush(); var value = list.Groups["text"].Value; var marker = list.Groups["marker"].Value; result.Add(Formatted("list-item", value, Math.Min(12, list.Groups["indent"].Length / 2 + 1), marker is "-" or "+" or "*" ? "•" : marker)); continue; }
            if (trimmed.StartsWith("> ")) { Flush(); var value = trimmed[2..]; result.Add(Formatted("quote", value)); continue; }
            paragraph.Add(line);
        }
        Flush(); return result;
    }
    private static FormattedBlock Formatted(string kind, string source, int level = 0, string? marker = null)
    {
        var runs = Inlines(source);
        // The renderer's fallback checks this plain-text contract. Keeping
        // Markdown delimiters here would make valid emphasis render literally.
        return new(kind, string.Concat(runs.Select(run => run.Text)), runs, level, Marker: marker);
    }
    private static List<FormattedInline> Inlines(string text)
    {
        var result = new List<FormattedInline>(); var offset = 0;
        foreach (Match match in Inline().Matches(text))
        {
            if (match.Index > offset) result.Add(new(text[offset..match.Index]));
            var value = match.Value;
            if (value[0] == '`') result.Add(new(value[1..^1], Code: true));
            else if (value.StartsWith("**") || value.StartsWith("__")) result.Add(new(value[2..^2], Bold: true));
            else if (value[0] is '*' or '_') result.Add(new(value[1..^1], Italic: true));
            else { var end = value.IndexOf("](", StringComparison.Ordinal); result.Add(new(value[1..end], Link: value[(end + 2)..^1])); }
            offset = match.Index + match.Length;
        }
        if (offset < text.Length) result.Add(new(text[offset..]));
        return result;
    }
}
