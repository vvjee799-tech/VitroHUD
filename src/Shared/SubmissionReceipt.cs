namespace VitroHUD;

internal sealed record SubmissionReceipt(string Request, string Text);
internal sealed class SubmissionReceipts
{
    private readonly Dictionary<string, SubmissionReceipt> entries = [];
    private static string Key(string provider, string source) => provider + ":" + source;
    internal string Begin(string provider, string source)
    {
        if (entries.Count >= 50) entries.Remove(entries.Keys.First());
        var id = Guid.NewGuid().ToString("N"); entries[Key(provider, source)] = new(id, "正在提交客户端…"); return id;
    }
    internal void Complete(string provider, string source, string request, BridgeReply reply)
    {
        var key = Key(provider, source);
        if (!entries.TryGetValue(key, out var current) || current.Request != request) return;
        entries[key] = new(request, reply.Ok ? "已提交客户端" : ReplyResult.Code(reply) == ResultCode.SubmissionUnknown ? "提交结果待确认，请检查客户端" : "未提交成功，请查看提示或日志");
    }
    internal SubmissionReceipt? For(string provider, string? source) => source != null && entries.TryGetValue(Key(provider, source), out var value) ? value : null;
}
