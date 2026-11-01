using System.Text.Json;
using System.Security.Cryptography;
using System.Text;

namespace VitroHUD;

public static class Wire
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..24];
}

public sealed record FormattedInline(string Text, bool Bold = false, bool Italic = false, bool Code = false, string? Link = null);
public sealed record FormattedBlock(string Kind, string Text, List<FormattedInline>? Inlines = null, int Level = 0, string? Language = null, string? Marker = null);
public sealed record MessagePart(string Kind, string Text, List<FormattedBlock>? Blocks = null);
public sealed record MessageBlock(string Id, string Role, string Text, List<MessagePart>? Parts = null);
public sealed record SourceCapabilities(bool CanRead = false, bool CanSend = false, bool CanStop = false, bool CanSelectModel = false);
public sealed record SourceSnapshot
{
    public string Provider { get; init; } = "ChatGPT";
    public string Status { get; init; } = "disconnected";
    public string Detail { get; init; } = "等待官方客户端";
    public string SourceKey { get; init; } = "";
    public long WindowHandle { get; init; }
    public string Title { get; init; } = "";
    public string Model { get; init; } = "由客户端控制";
    public string Draft { get; init; } = "";
    public bool IsGenerating { get; init; }
    public bool Partial { get; init; }
    public bool BackgroundConnected { get; init; }
    public bool ClientMinimized { get; init; }
    public SourceCapabilities Capabilities { get; init; } = new();
    public List<MessageBlock> Messages { get; init; } = [];
    public DateTimeOffset ObservedAt { get; init; } = DateTimeOffset.UtcNow;
}
public sealed record BridgeRequest(string Op, string SourceKey = "", string Text = "", string Value = "", bool AllowForegroundInput = false, long OriginWindow = 0, List<string>? MenuPath = null);
public sealed record BridgeReply(bool Ok, SourceSnapshot? Snapshot = null, string Error = "", List<string>? Choices = null);

public sealed class AppearanceSettings
{
    public const double DefaultCornerRadius = 11;
    private double cornerRadius = DefaultCornerRadius;
    public double Transparency { get; set; } = 70;
    public double Frosting { get; set; } = 45;
    public double EdgeHighlight { get; set; } = 60;
    public double CornerRadius
    {
        get => cornerRadius;
        set => cornerRadius = double.IsFinite(value) ? Math.Clamp(value, 0, 80) : DefaultCornerRadius;
    }
    public bool AlwaysOnTop { get; set; }
    public bool AllowForegroundInput { get; set; }
    public string Provider { get; set; } = "ChatGPT";
    public int Width { get; set; } = 420;
    public int Height { get; set; } = 820;
    public int? X { get; set; }
    public int? Y { get; set; }
    public int? CompactWidgetX { get; set; }
    public int? CompactWidgetY { get; set; }
    public int ClientHandleDock { get; set; } = 1;
    public double ClientHandleX { get; set; } = 1;
    public double ClientHandleY { get; set; } = .46;
}
