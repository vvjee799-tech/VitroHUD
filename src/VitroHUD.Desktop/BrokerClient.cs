using System.Diagnostics;
using System.Text;
using System.Text.Json;
namespace VitroHUD.Desktop;

internal sealed class BrokerClient(string provider) : IDisposable
{
    private Process? process;
    private readonly SemaphoreSlim gate = new(1, 1);
    public async Task<BridgeReply> RequestAsync(BridgeRequest request)
    {
        await gate.WaitAsync();
        try
        {
            EnsureStarted();
            if (request.AllowForegroundInput) NativeWindow.AllowSetForegroundWindow((uint)process!.Id);
            await process!.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request, Wire.Json));
            await process.StandardInput.FlushAsync();
            var line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(request.Op == "read" ? 9 : 20));
            return line == null ? new(false, Error: "连接进程已结束") : JsonSerializer.Deserialize<BridgeReply>(line, Wire.Json) ?? new(false, Error: "无效的连接响应");
        }
        catch (Exception ex) when (ex is TimeoutException or System.IO.IOException or InvalidOperationException or JsonException)
        {
            Stop(); return new(false, Error: request.Op == "send" ? "提交结果待确认，请检查原客户端。不会自动重发。" : "客户端读取超时，正在恢复连接");
        }
        finally { gate.Release(); }
    }
    private void EnsureStarted()
    {
        if (process?.HasExited == false) return;
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, "probe", "VitroHUD.Probe.exe");
        var info = new ProcessStartInfo(path) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8 };
        info.ArgumentList.Add(provider);
        process = Process.Start(info) ?? throw new InvalidOperationException("无法启动读取进程");
        process.ErrorDataReceived += (_, _) => { }; process.BeginErrorReadLine();
    }
    private void Stop() { try { if (process?.HasExited == false) process.Kill(true); process?.Dispose(); } catch { } process = null; }
    public void Dispose() => Stop();
}
