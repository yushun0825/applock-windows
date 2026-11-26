namespace AppLock.Core;

/// <summary>
/// ロックの判断をまとめる。Windows の API には触らず、アプリの操作・認証・時計を差し替えてテストする。
/// 呼ぶのはいつも同じスレッド(アプリでは画面のスレッド)。
///
/// ロックしたアプリは、Windows Hello で解除するまで画面に出さない:
/// - ロックしたアプリが起動したら、窓を作る前に止め、解除するまで止めておく(止めたアプリは窓を出せない)。
/// - 認証を求めている間も止める(確認の窓の後ろに何も出ない)。
/// 別の窓をかぶせて隠すことはしない(アプリが開いたように見えるため。Mac 版で 2026-11-15 に決めたこと)。
/// </summary>
public sealed class LockCoordinator
{
    /// <summary>認証できなかった直後は、前に出ても認証を求め直さない時間</summary>
    public static readonly TimeSpan FailureCooldown = TimeSpan.FromSeconds(1.5);

    private readonly IAppControl apps;
    private readonly IAuthenticator authenticator;
    private readonly IClock clock;
    private readonly Func<LockSettings> settings;

    private readonly UnlockLedger ledger = new();
    private readonly Dictionary<string, DateTimeOffset> cooldownUntil = new(StringComparer.Ordinal);
    /// <summary>起動してから一度も解除していないので、解除するまで止めておくアプリ</summary>
    private readonly HashSet<string> heldUntilUnlock = new(StringComparer.Ordinal);
    /// <summary>認証を待っているアプリ(認証の窓は 1 つずつ出す)</summary>
    private readonly List<string> waiting = [];

    public LockCoordinator(IAppControl apps, IAuthenticator authenticator, IClock clock, Func<LockSettings> settings)
    {
        this.apps = apps;
        this.authenticator = authenticator;
        this.clock = clock;
        this.settings = settings;
    }

    public Action<string> Log { get; set; } = _ => { };

    /// <summary>画面に出す状態(解除中・認証中)が変わったとき</summary>
    public event Action? Changed;

    /// <summary>認証して解除できたとき</summary>
    public event Action<string>? Unlocked;

    /// <summary>認証できなかったとき</summary>
    public event Action<string, AuthResult>? AuthenticationFailed;

    /// <summary>今、認証を求めているアプリ</summary>
    public string? Authenticating { get; private set; }

    /// <summary>今の認証の流れ(テストで終わるのを待つため)</summary>
    public Task? PromptTask { get; private set; }

    public IReadOnlyCollection<string> Held => heldUntilUnlock;
    public IReadOnlyList<string> Waiting => waiting;

    public bool HasUnlockedApps => settings().Apps.Any(a => ledger.IsUnlocked(a.Id, clock.Now));

    public bool IsUnlocked(string id) => ledger.IsUnlocked(id, clock.Now);

    /// <summary>解除中のアプリ(新しく起動したプロセスを止めずに通す)</summary>
    public IReadOnlySet<string> UnlockedIds() =>
        settings().LockedIds.Where(id => ledger.IsUnlocked(id, clock.Now)).ToHashSet(StringComparer.Ordinal);

    /// <summary>一度でも解除したアプリ(期限が切れても、使っている窓はいきなり隠さない)</summary>
    public IReadOnlySet<string> RevealedIds() => ledger.Ids.ToHashSet(StringComparer.Ordinal);

    public AppState StateOf(string id)
    {
        if (Authenticating == id) return new AppState(AppStateKind.Authenticating);
        if (ledger.LifetimeOf(id, clock.Now) is { } lifetime) return new AppState(AppStateKind.Unlocked, lifetime);
        return new AppState(AppStateKind.Locked);
    }

    private bool IsLocked(string id) => settings().LockedIds.Contains(id);

    private string NameOf(string id) => settings().Find(id)?.Name ?? id;

    private bool InCooldown(string id) => cooldownUntil.TryGetValue(id, out var until) && clock.Now < until;

    /// <summary>中で状態が変わったら知らせる</summary>
    private void Notifying(Action body)
    {
        var before = Snapshot();
        body();
        if (before != Snapshot()) Changed?.Invoke();
    }

    private (string, string?, string, string) Snapshot() =>
        (ledger.Fingerprint(clock.Now), Authenticating,
         string.Join(",", heldUntilUnlock.Order(StringComparer.Ordinal)), string.Join(",", waiting));

    // ---- 起きたこと ----

    /// <summary>アプリロックが起動したとき。すでに動いているロック中のアプリを隠す。
    /// 前のアプリロックが止めたまま終わっていたら、認証を求める。</summary>
    public void Start()
    {
        Notifying(() =>
        {
            foreach (var id in settings().LockedIds.Order(StringComparer.Ordinal))
            {
                if (!apps.IsRunning(id)) continue;
                if (apps.WasFrozenByPreviousRun(id))
                {
                    Log($"前のアプリロックが止めたままにしていた: {id}");
                    apps.Hide(id);
                    heldUntilUnlock.Add(id);
                    Enqueue(id);
                }
                else
                {
                    Log($"隠す(起動時に動いていた): {id}");
                    apps.Hide(id);
                }
            }
            PumpQueue();
        });
    }

    /// <summary>ロックするアプリが新しく起動した(ほかのアプリから起動されたプロセス)。
    /// 見張りは、解除中でなければ、知らせる前にもう止めている。</summary>
    public void AppLaunched(string id)
    {
        Notifying(() =>
        {
            if (!IsLocked(id) || ledger.IsUnlocked(id, clock.Now))
            {
                apps.Unfreeze(id);
                return;
            }
            if (heldUntilUnlock.Add(id)) Log($"起動したので、解除するまで止める: {id}");
            apps.Hide(id);
            apps.Freeze(id);
            if (Authenticating == id) return;
            Enqueue(id);
            PumpQueue();
        });
    }

    /// <summary>窓が前に出た。id はロックするアプリならその ID、ほかのアプリなら null。</summary>
    public void AppActivated(string? id)
    {
        Notifying(() =>
        {
            ledger.SwitchedTo(id);
            if (id is null || !IsLocked(id) || ledger.IsUnlocked(id, clock.Now)) return;
            if (Authenticating is not null)
            {
                // ほかのアプリの認証中は、隠すだけにする(認証の窓を重ねない)
                apps.Hide(id);
                if (Authenticating != id && heldUntilUnlock.Contains(id)) Enqueue(id);
                return;
            }
            if (InCooldown(id))
            {
                Log($"隠す(認証できなかった直後): {id}");
                apps.Hide(id);
                return;
            }
            waiting.Remove(id);
            RequestUnlock(id);
        });
    }

    /// <summary>ロックするアプリが窓を出した(見張りはもう隠している)</summary>
    public void AppWindowShown(string id)
    {
        if (!IsLocked(id) || ledger.IsUnlocked(id, clock.Now) || ledger.WasRevealed(id) || Authenticating == id) return;
        Log($"隠す(自分で窓を出した): {id}");
        apps.Hide(id);
    }

    /// <summary>ロックするアプリのプロセスがすべて終わった</summary>
    public void AppTerminated(string id)
    {
        Notifying(() =>
        {
            if (apps.IsRunning(id)) return;
            ledger.Remove(id);
            cooldownUntil.Remove(id);
            heldUntilUnlock.Remove(id);
            waiting.Remove(id);
            apps.Unfreeze(id);
            apps.StopKeepingHidden(id);
        });
    }

    /// <summary>PC のロック・スリープ</summary>
    public void SessionLocked()
    {
        Notifying(() =>
        {
            var keep = settings().RelockRule == RelockRule.WhenAppQuits;
            foreach (var id in ledger.ForgetAll(keepingUntilQuit: keep))
            {
                if (!IsLocked(id) || !apps.IsRunning(id)) continue;
                Log($"隠す(PC のロック): {id}");
                apps.Hide(id);
            }
        });
    }

    /// <summary>「すべてロック」: 解除中のアプリをロックし直す</summary>
    public void RelockAll()
    {
        Notifying(() =>
        {
            ledger.ForgetAll(keepingUntilQuit: false);
            foreach (var id in settings().LockedIds.Order(StringComparer.Ordinal))
            {
                if (Authenticating == id || !apps.IsRunning(id)) continue;
                Log($"隠す(すべてロック): {id}");
                apps.Hide(id);
            }
        });
    }

    /// <summary>見張り(1 秒ごと)。一度も解除していないロック中のアプリの窓が出ていたら隠す。待っている認証を進める。</summary>
    public void Sweep()
    {
        Notifying(() =>
        {
            foreach (var id in settings().LockedIds.Order(StringComparer.Ordinal))
            {
                if (Authenticating == id || ledger.WasRevealed(id) || !apps.IsVisible(id)) continue;
                Log($"隠す(見張り): {id}");
                apps.Hide(id);
            }
            PumpQueue();
        });
    }

    /// <summary>設定が変わったとき。ロックをやめたアプリは、止めていれば動かし、隠していれば出す。</summary>
    public void SettingsChanged()
    {
        Notifying(() =>
        {
            var locked = settings().LockedIds;
            foreach (var id in ledger.Ids)
            {
                if (!locked.Contains(id)) ledger.Remove(id);
            }
            foreach (var id in heldUntilUnlock.Where(id => !locked.Contains(id) && Authenticating != id).ToList())
            {
                Log($"ロックをやめたので動かす: {id}");
                heldUntilUnlock.Remove(id);
                apps.Reveal(id, activate: false);
                apps.Unfreeze(id);
            }
            waiting.RemoveAll(id => !locked.Contains(id));
            foreach (var id in apps.KeptHidden.Where(id => !locked.Contains(id) && Authenticating != id).ToList())
            {
                Log($"ロックをやめたので窓を出す: {id}");
                apps.Reveal(id, activate: false);
            }
        });
        Sweep();
    }

    // ---- 認証 ----

    private void Enqueue(string id)
    {
        if (!waiting.Contains(id)) waiting.Add(id);
    }

    /// <summary>待っている認証を 1 つ始める(認証中でなく、認証できなかった直後でなければ)</summary>
    private void PumpQueue()
    {
        if (Authenticating is not null) return;
        for (var i = 0; i < waiting.Count; i++)
        {
            var id = waiting[i];
            if (!IsLocked(id) || !apps.IsRunning(id))
            {
                waiting.RemoveAt(i--);
                continue;
            }
            if (ledger.IsUnlocked(id, clock.Now))
            {
                // 待っている間に、同じアプリをほかの経路で解除した
                waiting.RemoveAt(i--);
                if (heldUntilUnlock.Remove(id))
                {
                    apps.Reveal(id, activate: false);
                    apps.Unfreeze(id);
                }
                continue;
            }
            if (InCooldown(id)) continue;
            waiting.RemoveAt(i);
            RequestUnlock(id);
            return;
        }
    }

    private void RequestUnlock(string id)
    {
        Authenticating = id;
        apps.Hide(id);
        // 認証の間は止める(隠れるのを待ってから止まる)
        apps.Freeze(id);
        Log($"認証を求める: {id}");
        var message = $"「{NameOf(id)}」のロックを解除";
        PromptTask = RunPromptAsync(id, message);
    }

    private async Task RunPromptAsync(string id, string message)
    {
        AuthResult result;
        try
        {
            result = await authenticator.AuthenticateAsync(message);
        }
        catch (Exception e)
        {
            Log($"認証を呼び出せなかった: {e.GetType().Name}: {e.Message}");
            result = AuthResult.Failed;
        }
        FinishUnlock(id, result);
    }

    private void FinishUnlock(string id, AuthResult result)
    {
        var unlocked = false;
        Notifying(() =>
        {
            Authenticating = null;
            if (!IsLocked(id))
            {
                // 認証の途中でロックをやめた
                heldUntilUnlock.Remove(id);
                apps.Reveal(id, activate: result == AuthResult.Verified);
                apps.Unfreeze(id);
                return;
            }
            if (result == AuthResult.Verified)
            {
                if (!apps.IsRunning(id))
                {
                    // 認証の窓が出ている間にアプリが終了した。解除を残すと、次に開いたときに認証を求めなくなる
                    Log($"解除しない(認証の間にアプリが終了した): {id}");
                    heldUntilUnlock.Remove(id);
                    apps.StopKeepingHidden(id);
                    return;
                }
                ledger.Unlock(id, LifetimeForCurrentRule());
                Log($"解除: {id}");
                heldUntilUnlock.Remove(id);
                // 先に「解除した」と伝えてから動かす(動き出したアプリが出す窓を、見張りが隠さないように)
                apps.Reveal(id, activate: true);
                apps.Unfreeze(id);
                unlocked = true;
                return;
            }

            cooldownUntil[id] = clock.Now + FailureCooldown;
            if (settings().QuitAppOnFailure)
            {
                Log($"認証できなかったので終了させる({result}): {id}");
                heldUntilUnlock.Remove(id);
                apps.Quit(id);
            }
            else if (heldUntilUnlock.Contains(id))
            {
                // 起動してから解除していないアプリは、動かすと起動の続きで窓を出すので、止めたままにする
                Log($"認証できなかったので止めたままにする({result}): {id}");
            }
            else
            {
                Log($"認証できなかったので隠したままにする({result}): {id}");
                apps.Unfreeze(id);
            }
        });

        if (unlocked) Unlocked?.Invoke(id);
        else if (result != AuthResult.Verified) AuthenticationFailed?.Invoke(id, result);
        Notifying(PumpQueue);
    }

    private Lifetime LifetimeForCurrentRule()
    {
        var current = settings();
        return current.RelockRule switch
        {
            RelockRule.AfterInterval => Lifetime.UntilTime(clock.Now.AddMinutes(current.RelockMinutes)),
            RelockRule.WhenSwitchingAway => Lifetime.UntilSwitchAway,
            _ => Lifetime.UntilQuit,
        };
    }
}
