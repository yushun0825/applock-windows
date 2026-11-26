using System.Text.Json.Serialization;

namespace AppLock.Core;

/// <summary>ロックするアプリ。</summary>
public sealed record LockedApp
{
    /// <summary>画面に出す名前</summary>
    public string Name { get; init; } = "";

    /// <summary>実行ファイルの名前(例: LINE.exe)。この名前の実行ファイルのプロセスを、このアプリとして扱う
    /// (更新で入っているフォルダが変わるアプリがあるため、場所ではなく名前で見分ける)</summary>
    public string ExeName { get; init; } = "";

    /// <summary>アイコンを出す・アプリを開くための場所(最後に分かったもの)</summary>
    public string? Path { get; init; }

    /// <summary>ストアなどのパッケージのアプリのとき、パッケージ ファミリ名</summary>
    public string? PackageFamilyName { get; init; }

    /// <summary>パッケージのアプリを開くための ID(AppUserModelID)</summary>
    public string? AppUserModelId { get; init; }

    [JsonIgnore]
    public string Id => AppIdentity.MakeId(ExeName, PackageFamilyName);
}

public static class AppIdentity
{
    public static string MakeId(string exeName, string? packageFamilyName) =>
        string.IsNullOrEmpty(packageFamilyName)
            ? exeName.ToLowerInvariant()
            : $"{packageFamilyName.ToLowerInvariant()}!{exeName.ToLowerInvariant()}";
}

/// <summary>プロセスの実行ファイル名(とパッケージ)から、ロックするアプリを探す。</summary>
public sealed class AppMatcher
{
    public static readonly AppMatcher Empty = new([]);

    private readonly Dictionary<string, LockedApp[]> byExe;

    public AppMatcher(IEnumerable<LockedApp> apps)
    {
        byExe = apps
            .Where(a => !string.IsNullOrEmpty(a.ExeName))
            .GroupBy(a => a.ExeName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
    }

    public bool IsEmpty => byExe.Count == 0;

    /// <summary>この名前の実行ファイルは、ロックするアプリかもしれない</summary>
    public bool IsCandidate(string exeName) => byExe.ContainsKey(exeName);

    /// <summary>見分けるのにパッケージ ファミリ名が要る</summary>
    public bool NeedsPackage(string exeName) =>
        byExe.TryGetValue(exeName, out var apps) && apps.Any(a => !string.IsNullOrEmpty(a.PackageFamilyName));

    /// <summary>ロックするアプリの ID。パッケージを指定したものを先に見て、無ければ名前だけのもの。</summary>
    public string? Match(string exeName, string? packageFamilyName)
    {
        if (!byExe.TryGetValue(exeName, out var apps)) return null;
        LockedApp? loose = null;
        foreach (var app in apps)
        {
            if (string.IsNullOrEmpty(app.PackageFamilyName)) loose ??= app;
            else if (string.Equals(app.PackageFamilyName, packageFamilyName, StringComparison.OrdinalIgnoreCase)) return app.Id;
        }
        return loose?.Id;
    }
}
