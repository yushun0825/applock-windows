namespace AppLock.Core;

public enum AuthResult
{
    Verified,
    /// <summary>取り消した</summary>
    Canceled,
    /// <summary>通らなかった・回数を超えた・使用中だった</summary>
    Failed,
    /// <summary>Windows Hello が設定されていない・使えない</summary>
    Unavailable,
}

public interface IAuthenticator
{
    /// <summary>Windows Hello(PIN・指紋・顔)で本人か確かめる。message は確認の窓に出す文。</summary>
    Task<AuthResult> AuthenticateAsync(string message);
}

public interface IClock
{
    DateTimeOffset Now { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset Now => DateTimeOffset.Now;
}

/// <summary>ほかのアプリを隠す・止める・前に出す・終了させる。アプリでは Windows の API、テストでは偽物を使う。</summary>
public interface IAppControl
{
    bool IsRunning(string id);

    /// <summary>画面に窓が出ている</summary>
    bool IsVisible(string id);

    /// <summary>前に動いていたアプリロックが、止めたまま終わっていた</summary>
    bool WasFrozenByPreviousRun(string id);

    /// <summary>隠し続けているアプリ</summary>
    IReadOnlyCollection<string> KeptHidden { get; }

    /// <summary>出ている窓を隠し、そのあとに出した窓も隠し続ける(StopKeepingHidden か Reveal まで)</summary>
    void Hide(string id);

    void StopKeepingHidden(string id);

    /// <summary>アプリを止める(プロセスを一時停止する)。止めたアプリは窓を出せない。
    /// 窓が画面に出ているうちは止めず、隠れてから止める(止めたまま窓が残らないように)。</summary>
    void Freeze(string id);

    /// <summary>止めたのをやめる</summary>
    void Unfreeze(string id);

    /// <summary>隠した窓を出す。activate なら前に出す</summary>
    void Reveal(string id, bool activate);

    /// <summary>終了させる。止めたまま終わらせ、窓を出させない</summary>
    void Quit(string id);
}

public enum AppStateKind
{
    /// <summary>解除していない</summary>
    Locked,
    /// <summary>認証を求めている</summary>
    Authenticating,
    /// <summary>解除中</summary>
    Unlocked,
}

public readonly record struct AppState(AppStateKind Kind, Lifetime? Lifetime = null);
