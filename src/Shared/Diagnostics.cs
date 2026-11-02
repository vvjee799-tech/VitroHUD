using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VitroHUD;

internal enum LogEvent { AppStarted, AppStopped, AppError, ResourceError, ClientDiscovered, ClientLaunchRequested, ClientLaunched, ClientLaunchFailed, ClientObserved, ConnectionState, RequestStarted, RequestFinished, ProbeStarted, ProbeTransportError, ProbeError, SendInputStarted, SendInputVerified, SendInvoked, SendAcknowledged, ClientPageError }
internal enum ResultCode { None, Accepted, Rejected, SubmissionUnknown, DraftConflict, SourceChanged, ClientPageFailed, ClientUnavailable, TransportFailed, InvalidResponse, ProbeException }

internal static class ReplyResult
{
    internal static ResultCode Code(BridgeReply reply)
    {
        if (reply.Ok) return ResultCode.Accepted;
        if (Enum.TryParse<ResultCode>(reply.Code, out var code) && Enum.IsDefined(code) && code != ResultCode.None) return code;
        // Only classification is retained. Error text can contain client-supplied data.
        if (reply.Error.Contains("待确认")) return ResultCode.SubmissionUnknown;
        if (reply.Error.Contains("草稿")) return ResultCode.DraftConflict;
        if (reply.Error.Contains("会话已") || reply.Error.Contains("会话身份")) return ResultCode.SourceChanged;
        return ResultCode.Rejected;
    }
}

// This schema deliberately has no message, draft, title, URL or raw-error field.
internal sealed record DiagnosticEntry(DateTimeOffset Time, string AppVersion, int Process, LogEvent Event,
    string Provider, string Operation, ResultCode Code, string? Request, string? Source, long? DurationMs,
    string? State, string? ExceptionType, string? HResult, string? ClientVersion, string? ClientInstance,
    int? StringDrafts, int? ObjectDrafts);

internal sealed class DiagnosticJournal(string root, int maxFileBytes = 1_048_576, int maxFiles = 20, long maxTotalBytes = 20_971_520)
{
    private readonly object gate = new();
    private readonly string run = Guid.NewGuid().ToString("N");
    private string? current;
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };
    internal string Root => root;
    internal void Append(DiagnosticEntry entry)
    {
        try
        {
            lock (gate)
            {
                Directory.CreateDirectory(root);
                if (current == null || !Path.GetFileName(current).StartsWith($"vitrohud-{DateTime.UtcNow:yyyyMMdd}-", StringComparison.Ordinal) || new FileInfo(current) is { Exists: true, Length: var size } && size >= maxFileBytes)
                {
                    current = Path.Combine(root, $"vitrohud-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{Environment.ProcessId}-{Guid.NewGuid():N}.jsonl");
                }
                File.AppendAllText(current, JsonSerializer.Serialize(entry, Json) + "\n", new UTF8Encoding(false));
                Trim();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
    }
    private FileInfo[] Files() => Directory.Exists(root) ? new DirectoryInfo(root).GetFiles("vitrohud-*.jsonl")
        .Where(f => (f.Attributes & FileAttributes.ReparsePoint) == 0).OrderByDescending(f => f.LastWriteTimeUtc).ToArray() : [];
    private void Trim()
    {
        var files = Files(); long retained = 0; var count = 0;
        foreach (var file in files)
        {
            var keep = file.FullName == current || file.LastWriteTimeUtc >= DateTime.UtcNow.AddDays(-7) && count < maxFiles && retained + file.Length <= maxTotalBytes;
            if (keep) { count++; retained += file.Length; continue; }
            // Only our own non-link log files in this exact directory can expire.
            if (Path.GetDirectoryName(Path.GetFullPath(file.FullName)) == Path.GetFullPath(root)) file.Delete();
        }
    }
    internal string Recent(int limit = 120)
    {
        try
        {
            return string.Join("\n", Files().Take(4).SelectMany(f => File.ReadLines(f.FullName)).Select(line =>
            {
                try { return JsonSerializer.Deserialize<DiagnosticEntry>(line, Json); } catch (JsonException) { return null; }
            }).Where(e => e != null).OrderBy(e => e!.Time).TakeLast(limit).Select(e =>
                $"{e!.Time.ToLocalTime():MM-dd HH:mm:ss.fff}  {e.Provider}/{e.Operation}  {e.Event}  {e.Code}  请求:{e.Request ?? "-"}" +
                (e.State == null ? "" : $" 状态:{e.State}") + (e.DurationMs == null ? "" : $" 耗时:{e.DurationMs}ms") +
                (e.ExceptionType == null ? "" : $" 异常:{e.ExceptionType} {e.HResult}") +
                (e.ClientVersion == null ? "" : $" 客户端:{e.ClientVersion} 实例:{e.ClientInstance}") +
                (e.ObjectDrafts == null ? "" : $" 草稿类型计数:string={e.StringDrafts},object={e.ObjectDrafts}")));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "日志暂时不可读取。"; }
    }
    internal void Export(string destination)
    {
        lock (gate)
        {
            using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
            using var archive = new ZipArchive(output, ZipArchiveMode.Create);
            foreach (var file in Files().Take(maxFiles))
            {
                var entry = archive.CreateEntry(file.Name, CompressionLevel.Optimal);
                using var source = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var target = entry.Open(); source.CopyTo(target);
            }
            using var writer = new StreamWriter(archive.CreateEntry("README.txt").Open(), Encoding.UTF8);
            writer.Write($"VitroHUD local diagnostics\nVersion: {DiagnosticLog.Version}\nOS: {Environment.OSVersion.Version}\nExported: {DateTimeOffset.Now:O}\nNo conversation bodies, drafts, credentials, raw errors or full client paths are recorded.\nRequest IDs connect desktop and probe events. Client instance values are hashes of installation paths.\n");
        }
    }
    internal string SourceTag(string source) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(run + source)))[..12];
}

internal static class DiagnosticLog
{
    internal static string Version => typeof(DiagnosticLog).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
    private static readonly Lazy<DiagnosticJournal> journal = new(() => new(Path.Combine(Path.GetDirectoryName(AppPaths.FilePath("settings.json"))!, "Logs")));
    internal static DiagnosticJournal Journal => journal.Value;
    internal static void Write(LogEvent evt, string provider = "App", string operation = "none", ResultCode code = ResultCode.None,
        string? request = null, string? source = null, long? duration = null, string? state = null, Exception? exception = null,
        string? clientPath = null, int? stringDrafts = null, int? objectDrafts = null, DiagnosticJournal? sink = null)
    {
        try
        {
            provider = provider is "DeepSeek" or "Claude" or "ChatGPT" ? provider : "App";
            operation = operation is "read" or "send" or "stop" or "model-menu" or "choose-model" or "launch" or "none" ? operation : "other";
            state = state is "connected" or "connected-background" or "client-error" or "read-error" or "disconnected" or "paused" or "unrecognized" or "upstream-error" ? state : null;
            string? version = null, instance = null;
            if (clientPath != null)
            {
                version = FileVersionInfo.GetVersionInfo(clientPath).ProductVersion;
                if (version != null && !System.Text.RegularExpressions.Regex.IsMatch(version, @"^[0-9A-Za-z.+-]{1,100}$")) version = null;
                instance = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(clientPath).ToUpperInvariant())))[..12];
            }
            var target = sink ?? Journal;
            target.Append(new(DateTimeOffset.Now, Version, Environment.ProcessId, evt, provider, operation, code,
                Guid.TryParseExact(request, "N", out _) ? request : null, string.IsNullOrEmpty(source) ? null : target.SourceTag(source), duration,
                state, exception?.GetType().FullName, exception == null ? null : $"0x{exception.HResult:X8}", version, instance, stringDrafts, objectDrafts));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception or System.Security.SecurityException) { }
    }
}
