using System.Diagnostics;
using System.Text;
using System.Text.Json;
namespace VitroHUD.Desktop;

internal sealed class BrokerClient(string provider) : IDisposable
{
    private Process? process;
    private readonly SemaphoreSlim gate = new(1, 1);
    private string lastRead = "";
    public async Task<BridgeReply> RequestAsync(BridgeRequest request)
    {
        await gate.WaitAsync();
        request = request with { TraceId = Guid.TryParseExact(request.TraceId, "N", out _) ? request.TraceId : Guid.NewGuid().ToString("N") };
        var elapsed = Stopwatch.StartNew(); var attempted = false;
        if (request.Op != "read") DiagnosticLog.Write(LogEvent.RequestStarted, provider, request.Op, request: request.TraceId, source: request.SourceKey);
        try
        {
            EnsureStarted();
            if (request.AllowForegroundInput) NativeWindow.AllowSetForegroundWindow((uint)process!.Id);
            attempted = true;
            await process!.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request, Wire.Json));
            await process.StandardInput.FlushAsync();
            var line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(request.Op == "read" ? 9 : 20));
            var reply = line == null ? new(false, Error: request.Op == "send" ? "提交结果待确认，请检查原客户端。不会自动重发。" : "连接进程已结束", Code: request.Op == "send" ? nameof(ResultCode.SubmissionUnknown) : nameof(ResultCode.TransportFailed))
                : JsonSerializer.Deserialize<BridgeReply>(line, Wire.Json) ?? new BridgeReply(false, Error: request.Op == "send" ? "提交结果待确认，请检查原客户端。不会自动重发。" : "无效的连接响应", Code: (request.Op == "send" ? ResultCode.SubmissionUnknown : ResultCode.InvalidResponse).ToString());
            return Observe(reply);
        }
        catch (Exception ex) when (ex is TimeoutException or System.IO.IOException or InvalidOperationException or JsonException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            var unknown = request.Op == "send" && attempted;
            DiagnosticLog.Write(LogEvent.ProbeTransportError, provider, request.Op, unknown ? ResultCode.SubmissionUnknown : ResultCode.TransportFailed, request.TraceId, request.SourceKey, elapsed.ElapsedMilliseconds, exception: ex);
            Stop(); return Observe(new(false, Error: unknown ? "提交结果待确认，请检查原客户端。不会自动重发。" : "客户端连接未完成，正在恢复连接", Code: (unknown ? ResultCode.SubmissionUnknown : ResultCode.TransportFailed).ToString()));
        }
        finally { gate.Release(); }
        BridgeReply Observe(BridgeReply reply)
        {
            var code = ReplyResult.Code(reply);
            if (request.Op == "read" && reply.Snapshot?.Status == "client-error") code = ResultCode.ClientPageFailed;
            if (request.Op != "read") DiagnosticLog.Write(LogEvent.RequestFinished, provider, request.Op, code, request.TraceId, request.SourceKey, elapsed.ElapsedMilliseconds);
            else
            {
                var signature = $"{reply.Ok}|{code}|{reply.Snapshot?.Status}|{reply.Snapshot?.SourceKey}|{reply.Snapshot?.BackgroundConnected}";
                if (signature != lastRead)
                {
                    lastRead = signature;
                    DiagnosticLog.Write(LogEvent.ConnectionState, provider, "read", code, request.TraceId, reply.Snapshot?.SourceKey, elapsed.ElapsedMilliseconds, reply.Ok ? reply.Snapshot?.Status : "read-error");
                }
            }
            return reply with { Code = code.ToString() };
        }
    }
    private void EnsureStarted()
    {
        if (process?.HasExited == false) return;
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, "probe", "VitroHUD.Probe.exe");
        var info = new ProcessStartInfo(path) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8 };
        info.ArgumentList.Add(provider);
        process = Process.Start(info) ?? throw new InvalidOperationException("无法启动读取进程");
        DiagnosticLog.Write(LogEvent.ProbeStarted, provider);
        process.ErrorDataReceived += (_, _) => { }; process.BeginErrorReadLine();
    }
    private void Stop() { try { if (process?.HasExited == false) process.Kill(true); process?.Dispose(); } catch { } process = null; }
    public void Dispose() => Stop();
}
