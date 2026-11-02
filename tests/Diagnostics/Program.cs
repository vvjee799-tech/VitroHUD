using System.IO.Compression;
using System.Text.Json;
using VitroHUD;

var count = 0;
void Check(bool value, string label) { count++; if (!value) throw new Exception(label); }
var receipts = new SubmissionReceipts();
var first = receipts.Begin("DeepSeek", "session-a");
Check(receipts.For("DeepSeek", "session-a")!.Text == "正在提交客户端…", "pending is not a successful receipt");
Check(receipts.For("DeepSeek", "session-b") == null && receipts.For("Claude", "session-a") == null, "receipt follows exact source and provider");
receipts.Complete("DeepSeek", "session-a", first, new(true));
Check(receipts.For("DeepSeek", "session-a")!.Text == "已提交客户端", "acknowledged send shows receipt");
var second = receipts.Begin("DeepSeek", "session-a");
receipts.Complete("DeepSeek", "session-a", first, new(true));
Check(receipts.For("DeepSeek", "session-a")!.Request == second && receipts.For("DeepSeek", "session-a")!.Text == "正在提交客户端…", "late result cannot replace newer send");
receipts.Complete("DeepSeek", "session-a", second, new(false, Code: nameof(ResultCode.SubmissionUnknown)));
Check(receipts.For("DeepSeek", "session-a")!.Text.Contains("待确认"), "ambiguous send never reports submitted");
var third = receipts.Begin("DeepSeek", "session-a"); receipts.Complete("DeepSeek", "session-a", third, new(false, Error: "原客户端已有草稿"));
Check(receipts.For("DeepSeek", "session-a")!.Text.StartsWith("未提交成功"), "draft conflict is rejected");
Check(ReplyResult.Code(new(false, Error: "提交结果待确认")) == ResultCode.SubmissionUnknown, "legacy unknown receipt classification");
Check(ReplyResult.Code(new(false, Code: "made-up-code")) == ResultCode.Rejected, "unknown wire code is not treated as success");
var settings = JsonSerializer.Deserialize<AppearanceSettings>("{}")!;
Check(settings.TextSize == 20, "old settings retain original font size");
settings.TextSize = 1; Check(settings.TextSize == 12, "font floor"); settings.TextSize = 99; Check(settings.TextSize == 28, "font ceiling");
settings.TextSize = double.NaN; Check(settings.TextSize == 20, "nonfinite setting is safe"); settings.TextSize = 24;
Check(JsonSerializer.Deserialize<AppearanceSettings>(JsonSerializer.Serialize(settings))!.TextSize == 24, "font size persists");
var root = Path.Combine(Path.GetTempPath(), "VitroHUD-log-tests-" + Guid.NewGuid().ToString("N"));
try
{
    var journal = new DiagnosticJournal(root, maxFileBytes: 1, maxFiles: 3, maxTotalBytes: 8192);
    var secret = "private-prompt sk-test-only-token https://example.invalid/?secret=abc C:\\Users\\private-name";
    var request = Guid.NewGuid().ToString("N");
    DiagnosticLog.Write(LogEvent.ProbeError, provider: secret, operation: secret, request: secret, source: secret, state: secret, exception: new InvalidOperationException(secret), sink: journal);
    var raw = string.Join("\n", Directory.GetFiles(root).Select(File.ReadAllText));
    Check(!raw.Contains(secret) && !raw.Contains("sk-test-only") && !raw.Contains("private-name"), "error, path, title, prompt and credential text are never stored");
    Check(raw.Contains("InvalidOperationException") && raw.Contains("HResult"), "safe exception metadata retained");
    DiagnosticLog.Write(LogEvent.RequestStarted, "DeepSeek", "send", request: request, sink: journal);
    DiagnosticLog.Write(LogEvent.SendAcknowledged, "DeepSeek", "send", ResultCode.Accepted, request, sink: journal);
    Check(journal.Recent().Contains(request), "request correlation survives viewer formatting");
    var old = Directory.GetFiles(root).First(); File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-9));
    for (var i = 0; i < 10; i++) DiagnosticLog.Write(LogEvent.RequestFinished, "DeepSeek", "send", ResultCode.Accepted, request, sink: journal);
    Check(Directory.GetFiles(root, "vitrohud-*.jsonl").Length <= 3, "file rotation is bounded");
    Check(!File.Exists(old), "expired files removed");
    var zip = Path.Combine(root, "export.zip"); journal.Export(zip);
    using var archive = ZipFile.OpenRead(zip);
    Check(archive.Entries.Any(e => e.Name == "README.txt") && archive.Entries.Any(e => e.Name.EndsWith(".jsonl")), "diagnostic export is usable");
    Check(archive.Entries.All(e => !e.FullName.Contains("..") && !Path.IsPathRooted(e.FullName)), "export contains only local log basenames");
    Check(!new DiagnosticJournal(root).SourceTag("session-a").Equals(journal.SourceTag("session-a")), "source identifiers are salted per run");
    var blocked = Path.Combine(root, "not-a-folder"); File.WriteAllText(blocked, "fixture");
    DiagnosticLog.Write(LogEvent.AppError, sink: new DiagnosticJournal(blocked));
    Check(true, "log I/O failure cannot crash the operation");
}
finally { Directory.Delete(root, true); }
Console.WriteLine($"Diagnostics, submission receipt and text setting checks passed: {count}");
