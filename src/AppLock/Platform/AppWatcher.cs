using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using AppLock.Core;
using static AppLock.Native.Win32;

namespace AppLock.Platform;

/// <summary>
/// ロックするアプリのプロセスと窓を見張り、隠す・止める・出す・終了させる。
///
/// 見張りは専用のスレッドで動かす(画面のスレッドが忙しくても遅れないように):
/// - 窓のできごと(作った・出した・前に出た)をすぐ受け取る。プロセスが最初の窓を作った時点で見つかる。
/// - 1 秒ごとにプロセスの一覧を見る(窓を作らないうちに起動したものも見つける)。
/// - ロックするアプリのプロセスは、終わったらその場で知る(終了したら、すぐロックし直すため)。
/// ロックするアプリが新しく起動したら、解除中でなければ、その場で止めてから画面のスレッドに知らせる。
/// </summary>
internal sealed class AppWatcher : IAppControl, IDisposable
{
    private const uint PollIntervalMs = 1000;
    private const uint OpenAccess = PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_SUSPEND_RESUME | PROCESS_TERMINATE | SYNCHRONIZE;
    private static readonly TimeSpan ActivationWindow = TimeSpan.FromSeconds(4);

    private sealed class Proc
    {
        public required uint Pid { get; init; }
        public required long CreateTime { get; init; }
        public required string ExeName { get; init; }
        public uint ParentPid { get; init; }
        public string? AppId { get; set; }
        public IntPtr Handle { get; set; }
        public bool Frozen { get; set; }
        public RegisteredWaitHandle? ExitWait { get; set; }
    }

    /// <summary>プロセスのハンドルを待つための入れ物(ハンドルは閉じない)</summary>
    private sealed class ProcessWaitHandle : WaitHandle
    {
        public ProcessWaitHandle(IntPtr handle) => SafeWaitHandle = new SafeWaitHandle(handle, ownsHandle: false);
    }

    private readonly record struct HiddenWindow(IntPtr Hwnd, uint Pid);

    private sealed record Policy(AppMatcher Matcher, string MatcherKey, IReadOnlySet<string> PassThrough, IReadOnlySet<string> Revealed);

    private static readonly IReadOnlySet<string> NoIds = new HashSet<string>();

    private readonly object gate = new();
    private readonly Dictionary<uint, Proc> procs = [];
    /// <summary>止めておくアプリ(新しく起動したプロセスもすぐ止める)</summary>
    private readonly HashSet<string> frozenApps = new(StringComparer.Ordinal);
    private readonly HashSet<string> keptHidden = new(StringComparer.Ordinal);
    /// <summary>解除して窓を出したアプリ。画面のスレッドから新しい決まりが届く前でも、隠さず・止めずに通す</summary>
    private readonly HashSet<string> revealedNow = new(StringComparer.Ordinal);
    private readonly HashSet<string> unlockedNow = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<HiddenWindow>> hidden = new(StringComparer.Ordinal);
    private readonly HashSet<string> frozenByPreviousRun = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> pendingActivation = new(StringComparer.Ordinal);
    private readonly HashSet<uint> aliveScratch = [];
    private readonly List<Proc> exitedScratch = [];
    private readonly HashSet<uint> deniedLogged = [];

    private readonly Action<Action> post;
    private readonly Action<string> log;
    private readonly FrozenJournal journal;
    private readonly uint ownPid = (uint)Environment.ProcessId;
    private readonly uint sessionId;
    private readonly ManualResetEventSlim ready = new();

    private volatile Policy policy = new(AppMatcher.Empty, "", NoIds, NoIds);
    private byte[] buffer = new byte[1 << 20];
    private bool journalDirty;
    private FrozenJournal.Data? previousRun;
    private Thread? thread;
    private uint threadId;
    private WinEventProc? hookProc;

    public AppWatcher(Action<Action> post, Action<string> log, FrozenJournal journal)
    {
        if (!Environment.Is64BitProcess) throw new PlatformNotSupportedException("64 ビットの Windows が必要です");
        this.post = post;
        this.log = log;
        this.journal = journal;
        ProcessIdToSessionId(ownPid, out sessionId);
    }

    /// <summary>新しく起動した(ほかのアプリから起動されたプロセス)。解除中でなければ、もう止めてある</summary>
    public event Action<string>? AppLaunched;
    /// <summary>ロックするアプリが窓を出した(もう隠してある)</summary>
    public event Action<string>? AppWindowShown;
    /// <summary>窓が前に出た(ロックしないアプリなら null)</summary>
    public event Action<string?>? AppActivated;
    /// <summary>そのアプリのプロセスがすべて終わった</summary>
    public event Action<string>? AppTerminated;
    /// <summary>ロックするアプリが、管理者として動いているなどで止められない(実行ファイル名)</summary>
    public event Action<string>? AppNotLockable;

    // ---- 始める・終わる ----

    public void Start()
    {
        previousRun = journal.Load();
        thread = new Thread(Run) { IsBackground = true, Name = "AppLock watcher" };
        thread.Start();
        ready.Wait(TimeSpan.FromSeconds(10));
    }

    public void Dispose()
    {
        if (thread is null) return;
        PostThreadMessageW(threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        thread.Join(TimeSpan.FromSeconds(3));
        thread = null;
    }

    private void Run()
    {
        threadId = GetCurrentThreadId();
        hookProc = OnWinEvent;
        var showHook = SetWinEventHook(EVENT_OBJECT_CREATE, EVENT_OBJECT_SHOW, IntPtr.Zero, hookProc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        var foregroundHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, hookProc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        if (showHook == IntPtr.Zero || foregroundHook == IntPtr.Zero) log("窓のできごとを受け取れない(SetWinEventHook に失敗)");
        try
        {
            Poll(initial: true);
        }
        catch (Exception e)
        {
            log($"最初のプロセスの一覧で失敗: {e}");
        }
        ready.Set();
        SetTimer(IntPtr.Zero, UIntPtr.Zero, PollIntervalMs, IntPtr.Zero);
        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message == WM_TIMER && msg.hwnd == IntPtr.Zero)
            {
                try
                {
                    Poll(initial: false);
                }
                catch (Exception e)
                {
                    log($"プロセスの一覧で失敗: {e.Message}");
                }
                continue;
            }
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
        UnhookWinEvent(showHook);
        UnhookWinEvent(foregroundHook);
    }

    /// <summary>決まりを差し替える(画面のスレッドから)。ロックするアプリが変わったら、動いているプロセスを見分け直す。</summary>
    public void UpdatePolicy(IReadOnlyList<LockedApp> apps, IReadOnlySet<string> passThrough, IReadOnlySet<string> revealed)
    {
        var key = string.Join("|", apps.Select(a => a.Id).Order(StringComparer.Ordinal));
        var current = policy;
        var matcher = key == current.MatcherKey ? current.Matcher : new AppMatcher(apps);
        lock (gate)
        {
            policy = new Policy(matcher, key, passThrough, revealed);
            unlockedNow.IntersectWith(passThrough);
            revealedNow.IntersectWith(revealed);
            if (key != current.MatcherKey) Reclassify();
        }
    }

    /// <summary>アプリロックが終わるとき。止めたプロセスを全部動かし、隠した窓を全部出す(出すだけで前には出さない)。</summary>
    public void ReleaseAll()
    {
        lock (gate)
        {
            foreach (var proc in procs.Values.Where(p => p.Frozen))
            {
                NtResumeProcess(proc.Handle);
                proc.Frozen = false;
            }
            frozenApps.Clear();
            keptHidden.Clear();
            foreach (var window in hidden.Values.SelectMany(w => w))
            {
                if (IsWindow(window.Hwnd)) ShowWindowAsync(window.Hwnd, SW_SHOWNA);
            }
            hidden.Clear();
            journalDirty = true;
            SaveJournal();
        }
    }

    // ---- IAppControl(画面のスレッドから) ----

    public bool IsRunning(string id)
    {
        lock (gate)
        {
            return ProcessesOf(id).Any(p => WaitForSingleObject(p.Handle, 0) == WAIT_TIMEOUT);
        }
    }

    public bool IsVisible(string id)
    {
        lock (gate)
        {
            var pids = ProcessesOf(id).Select(p => p.Pid).ToHashSet();
            return pids.Count > 0 && VisibleWindows(pids).Count > 0;
        }
    }

    public bool WasFrozenByPreviousRun(string id)
    {
        lock (gate)
        {
            return frozenByPreviousRun.Contains(id);
        }
    }

    public IReadOnlyCollection<string> KeptHidden
    {
        get
        {
            lock (gate)
            {
                return keptHidden.Concat(hidden.Keys).Distinct().ToList();
            }
        }
    }

    public void Hide(string id)
    {
        lock (gate)
        {
            keptHidden.Add(id);
            unlockedNow.Remove(id);
            revealedNow.Remove(id);
            var pids = ProcessesOf(id).Select(p => p.Pid).ToHashSet();
            if (pids.Count > 0) HideWindows(id, VisibleWindows(pids));
        }
    }

    public void StopKeepingHidden(string id)
    {
        lock (gate)
        {
            keptHidden.Remove(id);
        }
    }

    public void Freeze(string id)
    {
        lock (gate)
        {
            frozenApps.Add(id);
            unlockedNow.Remove(id);
            foreach (var proc in ProcessesOf(id).ToList()) FreezeProcess(proc);
            SaveJournal();
        }
    }

    public void Unfreeze(string id)
    {
        lock (gate)
        {
            frozenApps.Remove(id);
            frozenByPreviousRun.Remove(id);
            foreach (var proc in ProcessesOf(id).Where(p => p.Frozen))
            {
                var status = NtResumeProcess(proc.Handle);
                if (status != 0) log($"動かせなかった: {proc.ExeName} {proc.Pid} 0x{status:X8}");
                proc.Frozen = false;
                journalDirty = true;
                log($"動かした: {proc.ExeName} {proc.Pid}");
            }
            SaveJournal();
        }
    }

    public void Reveal(string id, bool activate)
    {
        List<HiddenWindow> windows;
        bool frozen;
        lock (gate)
        {
            keptHidden.Remove(id);
            revealedNow.Add(id);
            if (activate)
            {
                unlockedNow.Add(id);
                pendingActivation[id] = DateTime.UtcNow + ActivationWindow;
            }
            windows = hidden.Remove(id, out var list) ? list : [];
            foreach (var proc in ProcessesOf(id)) AllowSetForegroundWindow(proc.Pid);
            frozen = ProcessesOf(id).Any(p => p.Frozen);
            journalDirty = true;
            SaveJournal();
        }
        // 下の窓から順に出す(止めているあいだは、動かしたときに出る)
        for (var i = windows.Count - 1; i >= 0; i--)
        {
            if (IsWindow(windows[i].Hwnd)) ShowWindowAsync(windows[i].Hwnd, SW_SHOWNA);
        }
        // 止めていなければ、いちばん上だった窓を前に出す(止めていれば、動かしたあとに出た窓を前に出す)
        if (activate && !frozen && windows.FirstOrDefault(w => IsWindow(w.Hwnd) && IsAppWindow(w.Hwnd)) is { Hwnd: not 0 } top)
        {
            SetForegroundWindow(top.Hwnd);
            lock (gate) pendingActivation.Remove(id);
        }
        if (windows.Count > 0) log($"窓を出した: {id}({windows.Count} 枚)");
    }

    public void Quit(string id)
    {
        lock (gate)
        {
            frozenApps.Remove(id);
            frozenByPreviousRun.Remove(id);
            foreach (var proc in ProcessesOf(id))
            {
                // 止めたまま終わらせる(動かすと、起動の続きで窓を出すため)
                if (!TerminateProcess(proc.Handle, 1)) log($"終了させられなかった: {proc.ExeName} {proc.Pid} {Marshal.GetLastWin32Error()}");
                else log($"終了させた: {proc.ExeName} {proc.Pid}");
            }
            hidden.Remove(id);
            journalDirty = true;
            SaveJournal();
        }
    }

    // ---- プロセスの一覧(見張りのスレッド) ----

    private unsafe void Poll(bool initial)
    {
        while (true)
        {
            fixed (byte* start = buffer)
            {
                var status = NtQuerySystemInformation(SystemProcessInformation, start, buffer.Length, out var needed);
                if (status == STATUS_INFO_LENGTH_MISMATCH)
                {
                    buffer = new byte[Math.Max(needed + (1 << 16), buffer.Length * 2)];
                    continue;
                }
                if (status != 0)
                {
                    log($"プロセスの一覧を読めない: 0x{status:X8}");
                    return;
                }
                lock (gate)
                {
                    Update(start, initial);
                }
                return;
            }
        }
    }

    /// <summary>SYSTEM_PROCESS_INFORMATION(64 ビット)の並びを読み、増えたプロセスと終わったプロセスを知る</summary>
    private unsafe void Update(byte* start, bool initial)
    {
        aliveScratch.Clear();
        var offset = 0;
        while (true)
        {
            var entry = start + offset;
            var next = *(uint*)entry;
            var createTime = *(long*)(entry + 0x20);
            var nameBytes = *(ushort*)(entry + 0x38);
            var name = *(char**)(entry + 0x40);
            var pid = (uint)(nuint)(*(IntPtr*)(entry + 0x50));
            var parent = (uint)(nuint)(*(IntPtr*)(entry + 0x58));
            var session = *(uint*)(entry + 0x64);

            if (pid != 0 && pid != ownPid && session == sessionId)
            {
                aliveScratch.Add(pid);
                if (!procs.TryGetValue(pid, out var known) || known.CreateTime != createTime)
                {
                    if (known is not null) Exited(known);
                    var exe = nameBytes > 0 && name != null ? new string(name, 0, nameBytes / 2) : "";
                    Register(pid, createTime, exe, parent, initial, "一覧");
                }
            }
            if (next == 0) break;
            offset += (int)next;
        }

        foreach (var proc in procs.Values)
        {
            if (!aliveScratch.Contains(proc.Pid)) exitedScratch.Add(proc);
        }
        foreach (var proc in exitedScratch) Exited(proc);
        exitedScratch.Clear();

        if (initial) RestoreHiddenWindowsFromPreviousRun();
        if (journalDirty) SaveJournal();
    }

    private void Register(uint pid, long createTime, string exeName, uint parentPid, bool initial, string source)
    {
        var proc = new Proc { Pid = pid, CreateTime = createTime, ExeName = exeName, ParentPid = parentPid };
        procs[pid] = proc;
        Classify(proc, initial, source);
    }

    /// <summary>ロックするアプリのプロセスか見分ける。そうなら手元にハンドルを持つ(止める・終わらせるため)。</summary>
    private void Classify(Proc proc, bool initial, string source)
    {
        var current = policy;
        if (string.IsNullOrEmpty(proc.ExeName) || !current.Matcher.IsCandidate(proc.ExeName)) return;

        var handle = OpenProcess(OpenAccess, false, proc.Pid);
        if (handle == IntPtr.Zero)
        {
            // 管理者として実行したアプリなどは止められない
            if (deniedLogged.Add(proc.Pid))
            {
                log($"開けないプロセス(管理者として実行など): {proc.ExeName} {proc.Pid} エラー {Marshal.GetLastWin32Error()}");
                var exeName = proc.ExeName;
                post(() => AppNotLockable?.Invoke(exeName));
            }
            return;
        }
        if (!GetProcessTimes(handle, out var created, out _, out _, out _) || created != proc.CreateTime)
        {
            CloseHandle(handle);
            return;
        }
        var package = current.Matcher.NeedsPackage(proc.ExeName) ? PackageFamilyName(handle) : null;
        var appId = current.Matcher.Match(proc.ExeName, package);
        if (appId is null)
        {
            CloseHandle(handle);
            return;
        }
        proc.Handle = handle;
        proc.AppId = appId;
        WatchExit(proc);

        if (initial)
        {
            if (previousRun?.Frozen.Any(e => e.Pid == proc.Pid && e.CreateTime == proc.CreateTime) == true)
            {
                // 前のアプリロックが止めたまま終わっていた。止めたままにして、認証を求める
                proc.Frozen = true;
                frozenApps.Add(appId);
                frozenByPreviousRun.Add(appId);
                journalDirty = true;
                log($"前の起動で止めたままのプロセス: {proc.ExeName} {proc.Pid}");
            }
            return;
        }
        OnNewProcess(proc, current, source);
    }

    /// <summary>終わったらその場で知る(0.1 秒も待たずにロックし直すため)</summary>
    private void WatchExit(Proc proc)
    {
        proc.ExitWait = ThreadPool.RegisterWaitForSingleObject(new ProcessWaitHandle(proc.Handle), (_, _) =>
        {
            lock (gate)
            {
                if (procs.TryGetValue(proc.Pid, out var known) && ReferenceEquals(known, proc)) Exited(proc);
            }
        }, null, Timeout.Infinite, executeOnlyOnce: true);
    }

    private static void ReleaseHandle(Proc proc)
    {
        proc.ExitWait?.Unregister(null);
        proc.ExitWait = null;
        if (proc.Handle != IntPtr.Zero) CloseHandle(proc.Handle);
        proc.Handle = IntPtr.Zero;
    }

    private bool IsPassThrough(string appId, Policy current) =>
        (current.PassThrough.Contains(appId) || unlockedNow.Contains(appId)) && !frozenApps.Contains(appId);

    private bool IsRevealed(string appId, Policy current) => current.Revealed.Contains(appId) || revealedNow.Contains(appId);

    private void OnNewProcess(Proc proc, Policy current, string source)
    {
        var appId = proc.AppId!;
        // 動いているそのアプリが起動した子のプロセス(ブラウザーのタブなど)は、起動として数えない
        var isChild = procs.TryGetValue(proc.ParentPid, out var parent) && parent != proc &&
                      parent.AppId == appId && parent.CreateTime <= proc.CreateTime;

        if (frozenApps.Contains(appId))
        {
            FreezeProcess(proc);
            if (!isChild) post(() => AppLaunched?.Invoke(appId));
            return;
        }
        if (IsPassThrough(appId, current) || isChild) return;

        log($"起動: {appId}({proc.ExeName} {proc.Pid}・{source}で見つけた)");
        frozenApps.Add(appId);
        FreezeProcess(proc);
        post(() => AppLaunched?.Invoke(appId));
    }

    private void Exited(Proc proc)
    {
        procs.Remove(proc.Pid);
        ReleaseHandle(proc);
        deniedLogged.Remove(proc.Pid);
        if (proc.AppId is not { } appId) return;
        if (proc.Frozen) journalDirty = true;
        if (hidden.TryGetValue(appId, out var list)) list.RemoveAll(w => w.Pid == proc.Pid);
        if (procs.Values.Any(p => p.AppId == appId)) return;
        hidden.Remove(appId);
        revealedNow.Remove(appId);
        unlockedNow.Remove(appId);
        journalDirty = true;
        post(() => AppTerminated?.Invoke(appId));
    }

    /// <summary>ロックするアプリが変わったとき。ロックをやめたアプリのプロセスは動かし、新しくロックするアプリは起動済みとして持つ。</summary>
    private void Reclassify()
    {
        var current = policy;
        foreach (var proc in procs.Values.ToList())
        {
            var stillLocked = proc.AppId is not null && current.Matcher.IsCandidate(proc.ExeName) &&
                              current.Matcher.Match(proc.ExeName, current.Matcher.NeedsPackage(proc.ExeName) ? PackageFamilyName(proc.Handle) : null) == proc.AppId;
            if (proc.AppId is not null && !stillLocked)
            {
                if (proc.Frozen)
                {
                    NtResumeProcess(proc.Handle);
                    proc.Frozen = false;
                    log($"ロックをやめたので動かした: {proc.ExeName} {proc.Pid}");
                }
                frozenApps.Remove(proc.AppId);
                ReleaseHandle(proc);
                proc.AppId = null;
                journalDirty = true;
            }
            if (proc.AppId is null) Classify(proc, initial: true, "一覧");
        }
        SaveJournal();
    }

    private IEnumerable<Proc> ProcessesOf(string id) => procs.Values.Where(p => p.AppId == id && p.Handle != IntPtr.Zero);

    // ---- 止める ----

    /// <summary>止める。窓が画面に出ているうちは止めず、隠してから止める(止めたまま窓が残らないように)</summary>
    private void FreezeProcess(Proc proc)
    {
        if (proc.Frozen || proc.Handle == IntPtr.Zero) return;
        var pids = new HashSet<uint> { proc.Pid };
        var visible = VisibleWindows(pids);
        if (visible.Count > 0)
        {
            HideWindows(proc.AppId!, visible);
            FreezeWhenHidden(proc);
            return;
        }
        var status = NtSuspendProcess(proc.Handle);
        if (status != 0)
        {
            log($"止められなかった: {proc.ExeName} {proc.Pid} 0x{status:X8}");
            return;
        }
        proc.Frozen = true;
        journalDirty = true;
        // 止める直前に窓を出していたら、いったん動かして隠してから止め直す
        visible = VisibleWindows(pids);
        if (visible.Count > 0)
        {
            NtResumeProcess(proc.Handle);
            proc.Frozen = false;
            HideWindows(proc.AppId!, visible);
            FreezeWhenHidden(proc);
            return;
        }
        log($"止めた: {proc.ExeName} {proc.Pid}");
    }

    /// <summary>窓が隠れるのを待ってから止める(隠す知らせは、そのアプリが受け取って初めて効くため)</summary>
    private void FreezeWhenHidden(Proc proc)
    {
        var appId = proc.AppId!;
        _ = Task.Run(async () =>
        {
            var pids = new HashSet<uint> { proc.Pid };
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var waited = 0;
                while (waited < 1500)
                {
                    bool done;
                    lock (gate)
                    {
                        if (!procs.ContainsKey(proc.Pid) || proc.Frozen || !frozenApps.Contains(appId)) return;
                        done = VisibleWindows(pids).Count == 0;
                    }
                    if (done) break;
                    await Task.Delay(5);
                    waited += 5;
                }
                lock (gate)
                {
                    if (!procs.ContainsKey(proc.Pid) || proc.Frozen || !frozenApps.Contains(appId)) return;
                    var visible = VisibleWindows(pids);
                    if (visible.Count == 0 || attempt == 2)
                    {
                        if (NtSuspendProcess(proc.Handle) == 0)
                        {
                            proc.Frozen = true;
                            journalDirty = true;
                            log(visible.Count == 0 ? $"隠れてから止めた: {proc.ExeName} {proc.Pid}" : $"窓が隠れないまま止めた: {proc.ExeName} {proc.Pid}");
                            SaveJournal();
                        }
                        return;
                    }
                    HideWindows(appId, visible);
                }
            }
        });
    }

    // ---- 窓 ----

    /// <summary>そのプロセスの、画面に出ている最上位の窓(上から順)</summary>
    private static List<HiddenWindow> VisibleWindows(HashSet<uint> pids)
    {
        var found = new List<HiddenWindow>();
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pids.Contains(pid) && IsWindowVisible(hwnd)) found.Add(new HiddenWindow(hwnd, pid));
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private void HideWindows(string appId, List<HiddenWindow> windows)
    {
        if (windows.Count == 0) return;
        if (!hidden.TryGetValue(appId, out var list)) hidden[appId] = list = [];
        foreach (var window in windows)
        {
            ShowWindowAsync(window.Hwnd, SW_HIDE);
            if (!list.Any(w => w.Hwnd == window.Hwnd)) list.Add(window);
        }
        journalDirty = true;
        log($"窓を隠した: {appId}({windows.Count} 枚)");
    }

    /// <summary>タスク バーに出る、ふつうのアプリの窓</summary>
    private static bool IsAppWindow(IntPtr hwnd)
    {
        var style = (long)GetWindowLongPtr(hwnd, GWL_STYLE);
        var exStyle = (long)GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        if ((style & WS_CHILD) != 0) return false;
        if ((exStyle & WS_EX_APPWINDOW) != 0) return true;
        return (exStyle & (WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE)) == 0 && GetWindow(hwnd, GW_OWNER) == IntPtr.Zero;
    }

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint eventThread, uint time)
    {
        if (idObject != OBJID_WINDOW || idChild != CHILDID_SELF || hwnd == IntPtr.Zero) return;
        try
        {
            switch (eventType)
            {
                case EVENT_OBJECT_CREATE:
                    OnWindowCreated(hwnd);
                    break;
                case EVENT_OBJECT_SHOW:
                    OnWindowShown(hwnd);
                    break;
                case EVENT_SYSTEM_FOREGROUND:
                    OnForeground(hwnd);
                    break;
            }
        }
        catch (Exception e)
        {
            log($"窓のできごとで失敗: {e.Message}");
        }
    }

    /// <summary>窓を作った。知らないプロセスなら(0.1 秒ごとの一覧より先に)見分ける</summary>
    private void OnWindowCreated(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0 || pid == ownPid) return;
        lock (gate)
        {
            Lookup(pid);
        }
    }

    private void OnWindowShown(IntPtr hwnd)
    {
        if (GetAncestor(hwnd, GA_ROOT) != hwnd) return;
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0 || pid == ownPid) return;
        string? shownApp = null;
        lock (gate)
        {
            var proc = Lookup(pid);
            if (proc?.AppId is not { } appId) return;
            var current = policy;
            if (IsPassThrough(appId, current) && !keptHidden.Contains(appId))
            {
                MaybeActivate(appId, hwnd);
                return;
            }
            var mustHide = keptHidden.Contains(appId) || frozenApps.Contains(appId) || !IsRevealed(appId, current);
            if (!mustHide) return;
            HideWindows(appId, [new HiddenWindow(hwnd, pid)]);
            if (frozenApps.Contains(appId) && !proc.Frozen) FreezeWhenHidden(proc);
            shownApp = appId;
        }
        post(() => AppWindowShown?.Invoke(shownApp!));
    }

    /// <summary>解除したばかりのアプリが最初に出した窓を前に出す(起動の続きで窓を出すため)</summary>
    private void MaybeActivate(string appId, IntPtr hwnd)
    {
        if (!pendingActivation.TryGetValue(appId, out var deadline)) return;
        if (DateTime.UtcNow > deadline)
        {
            pendingActivation.Remove(appId);
            return;
        }
        if (!IsAppWindow(hwnd)) return;
        pendingActivation.Remove(appId);
        var foreground = GetForegroundWindow();
        GetWindowThreadProcessId(foreground, out var foregroundPid);
        if (foreground == IntPtr.Zero || foregroundPid == ownPid || !procs.TryGetValue(foregroundPid, out var owner) || owner.AppId != appId)
        {
            SetForegroundWindow(hwnd);
        }
    }

    private void OnForeground(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0 || pid == ownPid) return;
        string? appId;
        lock (gate)
        {
            var proc = Lookup(pid);
            if (proc is null || SystemApps.IsIgnoredForSwitching(proc.ExeName, ClassName(hwnd))) return;
            appId = proc.AppId;
        }
        post(() => AppActivated?.Invoke(appId));
    }

    /// <summary>窓の持ち主のプロセス。知らないプロセス(または同じ番号の別のプロセス)なら調べて登録する</summary>
    private Proc? Lookup(uint pid)
    {
        if (procs.TryGetValue(pid, out var known))
        {
            // ロックするアプリのプロセスはハンドルを持っているので、番号が使い回されることはない。
            // それ以外は、起動した時刻で同じプロセスか確かめる
            if (known.Handle != IntPtr.Zero || SameProcess(pid, known.CreateTime)) return known;
            Exited(known);
        }
        RegisterFromWindow(pid);
        return procs.GetValueOrDefault(pid);
    }

    private static bool SameProcess(uint pid, long createTime)
    {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return true;
        try
        {
            return !GetProcessTimes(handle, out var created, out _, out _, out _) || created == createTime;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>窓から見つけたプロセスを、名前・起動した時刻・親を調べて登録する</summary>
    private void RegisterFromWindow(uint pid)
    {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return;
        try
        {
            if (!GetProcessTimes(handle, out var created, out _, out _, out _)) return;
            var path = ImagePath(handle);
            if (path is null) return;
            uint parent = 0;
            if (NtQueryInformationProcess(handle, 0, out var info, Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(), out _) == 0)
            {
                parent = (uint)(nuint)info.InheritedFromUniqueProcessId;
            }
            Register(pid, created, System.IO.Path.GetFileName(path), parent, initial: false, "窓");
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    // ---- 止めた記録(アプリロックが途中で終わっても、次の起動で戻せるように) ----

    private void SaveJournal()
    {
        if (!journalDirty) return;
        journalDirty = false;
        var data = new FrozenJournal.Data
        {
            Frozen = procs.Values.Where(p => p.Frozen && p.AppId is not null)
                .Select(p => new FrozenJournal.ProcessEntry(p.Pid, p.CreateTime, p.AppId!)).ToList(),
            Hidden = hidden.SelectMany(pair => pair.Value.Select(w =>
                    new FrozenJournal.WindowEntry((long)w.Hwnd, w.Pid, procs.TryGetValue(w.Pid, out var p) ? p.CreateTime : 0, pair.Key)))
                .ToList(),
        };
        journal.Save(data, log);
    }

    /// <summary>前のアプリロックが隠したままにした窓。まだロックするアプリなら隠したまま持ち、そうでなければ出す。</summary>
    private void RestoreHiddenWindowsFromPreviousRun()
    {
        if (previousRun is null) return;
        foreach (var entry in previousRun.Hidden)
        {
            var hwnd = (IntPtr)entry.Hwnd;
            if (!procs.TryGetValue(entry.Pid, out var proc) || proc.CreateTime != entry.CreateTime || !IsWindow(hwnd)) continue;
            GetWindowThreadProcessId(hwnd, out var owner);
            if (owner != entry.Pid) continue;
            if (proc.AppId == entry.AppId)
            {
                if (!hidden.TryGetValue(entry.AppId, out var list)) hidden[entry.AppId] = list = [];
                if (!list.Any(w => w.Hwnd == hwnd)) list.Add(new HiddenWindow(hwnd, entry.Pid));
            }
            else
            {
                ShowWindowAsync(hwnd, SW_SHOWNA);
            }
        }
        // ロックをやめていたアプリの、止めたままのプロセスは動かす
        foreach (var entry in previousRun.Frozen)
        {
            if (procs.TryGetValue(entry.Pid, out var proc) && proc.CreateTime == entry.CreateTime && proc.AppId is null)
            {
                var handle = OpenProcess(PROCESS_SUSPEND_RESUME, false, entry.Pid);
                if (handle == IntPtr.Zero) continue;
                NtResumeProcess(handle);
                CloseHandle(handle);
                log($"前の起動で止めたままのプロセスを動かした(ロックをやめていた): {proc.ExeName} {proc.Pid}");
            }
        }
        previousRun = null;
        journalDirty = true;
    }
}
