using System.Text.Json;
using System.Text.Json.Serialization;

namespace AppLock.Core;

/// <summary>解除したあと、いつロックし直すか。</summary>
public enum RelockRule
{
    /// <summary>そのアプリを終了したとき</summary>
    WhenAppQuits,
    /// <summary>解除してから一定時間がたったとき</summary>
    AfterInterval,
    /// <summary>ほかのアプリに切り替えたとき</summary>
    WhenSwitchingAway,
}

/// <summary>アプリロックの設定。保存して公開したものは変えず、変えるときは写してから変える。</summary>
public sealed class LockSettings
{
    public const int MinMinutes = 1;
    public const int MaxMinutes = 60;

    private List<LockedApp> apps = [];
    private HashSet<string>? lockedIds;

    public List<LockedApp> Apps
    {
        get => apps;
        set { apps = value ?? []; lockedIds = null; }
    }

    public RelockRule RelockRule { get; set; } = RelockRule.WhenAppQuits;
    public int RelockMinutes { get; set; } = 5;
    public bool QuitAppOnFailure { get; set; } = true;
    public bool ProtectSelf { get; set; } = true;
    /// <summary>窓を閉じても動き続けることを一度知らせたか</summary>
    public bool ToldAboutTray { get; set; }

    [JsonIgnore]
    public IReadOnlySet<string> LockedIds => lockedIds ??= Apps.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);

    public LockedApp? Find(string id) => Apps.FirstOrDefault(a => a.Id == id);

    public LockSettings Clone() => new()
    {
        Apps = [.. Apps],
        RelockRule = RelockRule,
        RelockMinutes = RelockMinutes,
        QuitAppOnFailure = QuitAppOnFailure,
        ProtectSelf = ProtectSelf,
        ToldAboutTray = ToldAboutTray,
    };

    /// <summary>保存する前・読んだあとに、ありえない値を直す</summary>
    public void Normalize()
    {
        RelockMinutes = Math.Clamp(RelockMinutes, MinMinutes, MaxMinutes);
        if (!Enum.IsDefined(RelockRule)) RelockRule = RelockRule.WhenAppQuits;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        Apps = Apps
            .Where(a => a is not null && SystemApps.CanLock(a.ExeName))
            .Select(a => string.IsNullOrWhiteSpace(a.Name) ? a with { Name = System.IO.Path.GetFileNameWithoutExtension(a.ExeName) } : a)
            .Where(a => seen.Add(a.Id))
            .ToList();
    }
}

public static class SettingsJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new TolerantEnumConverter<RelockRule>(RelockRule.WhenAppQuits) },
    };

    /// <summary>あとから項目を足しても前の版で保存した設定を読めるように、無い項目は既定の値にする</summary>
    public static LockSettings Parse(string json)
    {
        var settings = JsonSerializer.Deserialize<LockSettings>(json, Options) ?? new LockSettings();
        settings.Normalize();
        return settings;
    }

    public static string Serialize(LockSettings settings) => JsonSerializer.Serialize(settings, Options);
}

/// <summary>知らない値は既定の値として読む(設定ごと読めなくならないように)</summary>
public sealed class TolerantEnumConverter<T>(T fallback) : JsonConverter<T> where T : struct, Enum
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String && Enum.TryParse<T>(reader.GetString(), ignoreCase: true, out var value) && Enum.IsDefined(value))
            return value;
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var number) && Enum.IsDefined(typeof(T), number))
            return (T)Enum.ToObject(typeof(T), number);
        reader.Skip();
        return fallback;
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
