namespace AppLock.Core;

public enum LifetimeKind
{
    /// <summary>そのアプリを終了するまで</summary>
    UntilQuit,
    /// <summary>その時刻まで</summary>
    Until,
    /// <summary>ほかのアプリに切り替えるまで</summary>
    UntilSwitchAway,
}

/// <summary>解除がいつまで続くか</summary>
public readonly record struct Lifetime(LifetimeKind Kind, DateTimeOffset End = default)
{
    public static Lifetime UntilQuit => new(LifetimeKind.UntilQuit);
    public static Lifetime UntilSwitchAway => new(LifetimeKind.UntilSwitchAway);
    public static Lifetime UntilTime(DateTimeOffset end) => new(LifetimeKind.Until, end);
}

/// <summary>どのアプリのロックを、いつまで解除しているかを覚える。</summary>
public sealed class UnlockLedger
{
    private sealed class Entry(Lifetime lifetime)
    {
        public Lifetime Lifetime { get; } = lifetime;
        public bool Expired { get; set; }
    }

    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);

    public IReadOnlyList<string> Ids => entries.Keys.Order(StringComparer.Ordinal).ToList();
    public bool IsEmpty => entries.Count == 0;

    public void Unlock(string id, Lifetime lifetime) => entries[id] = new Entry(lifetime);

    /// <summary>今、認証なしで使わせてよいか</summary>
    public bool IsUnlocked(string id, DateTimeOffset now)
    {
        if (!entries.TryGetValue(id, out var entry) || entry.Expired) return false;
        return entry.Lifetime.Kind != LifetimeKind.Until || now < entry.Lifetime.End;
    }

    /// <summary>解除中なら、いつまでか</summary>
    public Lifetime? LifetimeOf(string id, DateTimeOffset now) =>
        IsUnlocked(id, now) ? entries[id].Lifetime : null;

    /// <summary>一度でも解除して画面に出したか(期限が切れたものも含む)。
    /// 期限が切れても、使っている最中の窓をいきなり隠さないために分けて持つ。</summary>
    public bool WasRevealed(string id) => entries.ContainsKey(id);

    /// <summary>あるアプリが前に出た(id が null ならロックしないアプリ)。
    /// 「ほかのアプリに切り替えるまで」の解除は、ほかのアプリのぶんを切らす。</summary>
    public void SwitchedTo(string? id)
    {
        foreach (var (key, entry) in entries)
        {
            if (key != id && entry.Lifetime.Kind == LifetimeKind.UntilSwitchAway) entry.Expired = true;
        }
    }

    public void Remove(string id) => entries.Remove(id);

    /// <summary>解除をまとめて取り消す。取り消したアプリを返す。</summary>
    public IReadOnlyList<string> ForgetAll(bool keepingUntilQuit)
    {
        var forgotten = entries
            .Where(e => !(keepingUntilQuit && e.Value.Lifetime.Kind == LifetimeKind.UntilQuit))
            .Select(e => e.Key)
            .Order(StringComparer.Ordinal)
            .ToList();
        foreach (var key in forgotten) entries.Remove(key);
        return forgotten;
    }

    /// <summary>状態が変わったかを比べるための写し</summary>
    public string Fingerprint(DateTimeOffset now) =>
        string.Join("|", entries.OrderBy(e => e.Key, StringComparer.Ordinal)
            .Select(e => $"{e.Key}:{e.Value.Lifetime.Kind}:{(IsUnlocked(e.Key, now) ? 1 : 0)}"));
}
