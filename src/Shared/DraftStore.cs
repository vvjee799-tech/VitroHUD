namespace VitroHUD;

// Draft ownership survives delayed binding, tab changes and asynchronous receipts.
internal sealed class DraftStore
{
    private readonly Dictionary<string, string> drafts = [];
    private static string Key(string provider, string? source) => provider + ":" + (string.IsNullOrEmpty(source) ? "unbound" : source);
    public string Get(string provider, string? source) => drafts.GetValueOrDefault(Key(provider, source), "");
    public void Put(string provider, string? source, string text) => drafts[Key(provider, source)] = text;
    public string Bind(string provider, string? previousSource, string newSource, string text)
    {
        Put(provider, previousSource, text);
        var key = Key(provider, newSource);
        if (string.IsNullOrEmpty(previousSource) && !string.IsNullOrEmpty(newSource))
        {
            var pending = Get(provider, null);
            // A saved target draft takes precedence. Keep any different pending draft available.
            if (!drafts.TryGetValue(key, out var target) || target.Length == 0) { drafts[key] = pending; drafts.Remove(Key(provider, null)); }
            else if (target == pending || pending.Length == 0) drafts.Remove(Key(provider, null));
        }
        return Get(provider, newSource);
    }
    public bool Acknowledge(string provider, string source, string submitted)
    {
        var key = Key(provider, source);
        if (drafts.GetValueOrDefault(key) != submitted) return false;
        drafts[key] = ""; return true;
    }
}
