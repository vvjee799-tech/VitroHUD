using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace VitroHUD.Desktop;

// Render only the structured text supplied by the probe; never interpret it as XAML or HTML.
internal static class TranscriptRenderer
{
    private static readonly FontFamily CodeFont = new("Cascadia Mono, Consolas");

    public static FrameworkElement RenderPart(MessagePart part, ResourceDictionary colors)
    {
        var body = part.Kind == "body";
        var reasoning = part.Kind == "reasoning";
        var style = new TextStyle(body ? 20 : reasoning ? 14 : 12, body ? 32 : reasoning ? 23 : 19,
            (Brush)colors[body ? "MainTextBrush" : "SecondaryTextBrush"], colors);

        if (part.Blocks is not { Count: > 0 }) return MakeText(part.Text, style);

        var panel = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Stretch };
        for (var i = 0; i < part.Blocks.Count; i++)
        {
            var block = part.Blocks[i];
            if (block.Kind == "table-row")
            {
                var rows = new List<FormattedBlock>();
                while (i < part.Blocks.Count && part.Blocks[i].Kind == "table-row") rows.Add(part.Blocks[i++]);
                i--;
                panel.Children.Add(MakeTable(rows, style));
                continue;
            }

            panel.Children.Add(block.Kind switch
            {
                "paragraph" => MakeFormattedText(block, style),
                "heading" => MakeFormattedText(block, style, heading: true),
                "code" => MakeCode(block, style),
                "list-item" => MakeListItem(block, style),
                "quote" => new Border
                {
                    BorderBrush = (Brush)colors["AccentTextBrush"],
                    BorderThickness = new Thickness(2, 0, 0, 0),
                    Padding = new Thickness(12, 0, 0, 0),
                    Child = MakeFormattedText(block, style)
                },
                // A future or malformed kind is still displayed as inert text.
                _ => MakeText(block.Text, style)
            });
        }
        return panel;
    }

    private static TextBlock MakeText(string text, TextStyle style, bool wrap = true) => new()
    {
        Text = text ?? "",
        FontSize = style.FontSize,
        LineHeight = style.LineHeight,
        Foreground = style.Foreground,
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        IsTextSelectionEnabled = true,
        HorizontalAlignment = HorizontalAlignment.Stretch
    };

    private static TextBlock MakeFormattedText(FormattedBlock block, TextStyle style, bool heading = false)
    {
        var text = MakeText("", style);
        if (heading) text.FontWeight = FontWeights.SemiBold;

        // If the inline representation is incomplete, show the source fallback instead of losing text.
        if (block.Inlines is not { Count: > 0 } || string.Concat(block.Inlines.Select(run => run.Text)) != (block.Text ?? ""))
        {
            text.Text = block.Text ?? "";
            return text;
        }

        foreach (var inline in block.Inlines)
        {
            var run = new Run { Text = inline.Text ?? "" };
            if (inline.Bold) run.FontWeight = FontWeights.Bold;
            if (inline.Italic) run.FontStyle = Windows.UI.Text.FontStyle.Italic;
            if (inline.Code)
            {
                run.FontFamily = CodeFont;
                run.Foreground = (Brush)style.Colors["InlineCodeBrush"];
            }

            if (Uri.TryCreate(inline.Link, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                var link = new Hyperlink { NavigateUri = uri, Foreground = (Brush)style.Colors["AccentTextBrush"] };
                link.Inlines.Add(run);
                text.Inlines.Add(link);
            }
            else text.Inlines.Add(run);
        }
        return text;
    }

    private static FrameworkElement MakeCode(FormattedBlock block, TextStyle style)
    {
        var code = MakeText(block.Text, style, wrap: false);
        code.FontFamily = CodeFont;
        var content = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Stretch };
        if (!string.IsNullOrWhiteSpace(block.Language))
            content.Children.Add(MakeText(block.Language, style with { FontSize = 12, LineHeight = 19, Foreground = (Brush)style.Colors["SecondaryTextBrush"] }));
        content.Children.Add(new ScrollViewer
        {
            HorizontalScrollMode = ScrollMode.Enabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = code
        });
        return new Border
        {
            Background = (Brush)style.Colors["CodeSurfaceBrush"],
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8, 10, 8),
            Child = content
        };
    }

    private static FrameworkElement MakeListItem(FormattedBlock block, TextStyle style)
    {
        var row = new Grid { Margin = new Thickness(Math.Clamp(block.Level - 1, 0, 3) * 14, 0, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        var marker = MakeText(string.IsNullOrEmpty(block.Marker) ? "•" : block.Marker, style);
        marker.Margin = new Thickness(0, 0, 8, 0);
        row.Children.Add(marker);
        var text = MakeFormattedText(block, style);
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        return row;
    }

    private static FrameworkElement MakeTable(List<FormattedBlock> rows, TextStyle style)
    {
        var cells = rows.Select(row => (row.Text ?? "").Split('\t')).ToArray();
        var columns = cells.Max(row => row.Length);
        // A sparse or very wide untrusted table must not create a quadratic number of controls.
        if (columns > 16 || (long)columns * cells.Length > 2048)
        {
            var fallback = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var row in rows) fallback.Children.Add(MakeText(row.Text, style));
            return fallback;
        }
        var table = new Grid();
        for (var column = 0; column < columns; column++)
            table.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 100, MaxWidth = 220 });
        for (var row = 0; row < cells.Length; row++)
        {
            table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (var column = 0; column < columns; column++)
            {
                var cell = new Border
                {
                    BorderBrush = (Brush)style.Colors["TableRuleBrush"],
                    BorderThickness = new Thickness(0, 0, 1, 1),
                    Padding = new Thickness(7, 4, 7, 4),
                    Child = MakeText(column < cells[row].Length ? cells[row][column] : "", style)
                };
                Grid.SetRow(cell, row);
                Grid.SetColumn(cell, column);
                table.Children.Add(cell);
            }
        }
        return new ScrollViewer
        {
            HorizontalScrollMode = ScrollMode.Enabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = table
        };
    }

    private readonly record struct TextStyle(double FontSize, double LineHeight, Brush Foreground, ResourceDictionary Colors);
}
