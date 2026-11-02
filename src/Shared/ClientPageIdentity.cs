using System.Text.RegularExpressions;

namespace VitroHUD;

internal static class ClientPageIdentity
{
    internal static bool Eligible(string provider, string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var url) || url.UserInfo.Length > 0) return false;
        if (provider == "DeepSeek") return url.Scheme == "dsh-app" && url.Host == "app"
            || url.Scheme == "app" && url.Host is "app" or "localhost" or "dsh" or "-"
            || url.IsFile && url.Host.Length == 0
            || url.Scheme == "http" && url.Host is "127.0.0.1" or "localhost" or "[::1]";
        if (provider == "Claude") return Regex.IsMatch(value, @"^app://localhost/(?:epitaxy|code|claude-code-desktop)(?:/|$)", RegexOptions.IgnoreCase);
        return provider == "ChatGPT" && url.Scheme == "app";
    }
    internal static bool SameTitle(string provider, string left, string right)
    {
        string Normalize(string value)
        {
            value = Regex.Replace(value, @"\s+", " ").Trim();
            if (provider == "DeepSeek") value = Regex.Replace(value, @"\s+[—–-]\s+(?:DeepSeek[\s_-]*Harness|DSH[\s_-]*Desktop)\s*$", "", RegexOptions.IgnoreCase);
            return value;
        }
        return !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right) && Normalize(left) == Normalize(right);
    }
    internal static bool DebugEndpoint(string value, int port, string kind)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var url) && url.Scheme == "ws" && url.Host == "127.0.0.1"
            && url.Port == port && url.UserInfo.Length == 0 && url.Query.Length == 0 && url.Fragment.Length == 0
            && url.AbsolutePath.StartsWith("/devtools/" + kind + "/", StringComparison.Ordinal);
    }
}
