using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AppLock.Core;
using AppLock.Native;
using AppLock.Platform;
using AppLock.UI;
using Microsoft.Win32;

namespace AppLock;

/// <summary>アプリロックの全体をつなぐ(見張り・判断・認証・設定・通知領域・窓)。画面のスレッドで使う。</summary>
internal sealed class AppController : IDisposable
{
    private readonly App application;
    private readonly Dispatcher dispatcher;
    private readonly AppWatcher watcher;
    private readonly IAuthenticator authenticator;
    private readonly bool fakeAuthentication;
    private readonly TrayIcon tray;
    private readonly TrayMenu menu;
    private readonly DispatcherTimer timer;
    private MainWindow? window;
    private bool disposed;

    public AppController(App application, bool background)
    {
        this.application = application;
        dispatcher = application.Dispatcher;
        var dataDirectory = Paths.DataDirectory;
        Log.Init(dataDirectory);
        Log.Write($"起動: アプリロック {Version}({RuntimeInformation.ProcessArchitecture}・Windows {Environment.OSVersion.Version})" +
                  (Testing.Enabled ? "・確かめるための組み立て" : ""));

        Store = new SettingsStore(dataDirectory, Log.Write);
        if (!Testing.Enabled && Environment.ProcessPath is { } exe)
        {
            // 初めての起動では、サインインしたときに開くようにする
            if (!Store.ExistedAtStart)
            {
                StartupRegistration.Enable(exe);
                Store.Save();
            }
            else
            {
                StartupRegistration.Refresh(exe);
            }
        }

        var fake = Testing.FakeAuthenticator(Log.Write);
        fakeAuthentication = fake is not null;
        authenticator = fake ?? new HelloAuthenticator(Log.Write);

        watcher = new AppWatcher(action => dispatcher.BeginInvoke(action), Log.Write, new FrozenJournal(dataDirectory));
        Coordinator = new LockCoordinator(watcher, authenticator, new SystemClock(), () => Store.Current) { Log = Log.Write };
        Coordinator.Changed += OnCoordinatorChanged;
        Coordinator.AuthenticationFailed += OnAuthenticationFailed;
        Coordinator.Unlocked += _ => authenticationUnavailable = false;
        watcher.AppLaunched += Coordinator.AppLaunched;
        watcher.AppActivated += Coordinator.AppActivated;
        watcher.AppWindowShown += Coordinator.AppWindowShown;
        watcher.AppTerminated += Coordinator.AppTerminated;
        watcher.AppNotLockable += OnAppNotLockable;
        Store.Changed += OnSettingsChanged;

        PublishPolicy();
        watcher.Start();
        Coordinator.Start();
        PublishPolicy();
        Log.Write($"ロックするアプリ: {Store.Current.Apps.Count} 個");

        tray = new TrayIcon(LoadResource("Assets/AppLock.ico"), "アプリロック");
        menu = new TrayMenu(this);
        tray.LeftClick += ShowMainWindow;
        tray.RightClick += (_, _) => menu.Show();

        timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal, (_, _) => Tick(), dispatcher);
        timer.Start();
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionEnding += OnSessionEnding;

        _ = RefreshHelloAsync();
        UpdateTooltip();
        if (!background) ShowMainWindow();
    }

    public static string Version =>
        typeof(AppController).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "1.0.0";

    public SettingsStore Store { get; }
    public LockCoordinator Coordinator { get; }
    public IconCache Icons { get; } = new();

    /// <summary>Windows Hello の状態(まだ分からなければ null)</summary>
    public HelloAvailability? Hello { get; private set; }

    /// <summary>窓に出すものが変わったとき</summary>
    public event Action? StateChanged;

    /// <summary>本人確認の方法が無かった(Windows Hello もパスワードも無いアカウント)。直せるように、設定の変更に認証を求めない</summary>
    private bool authenticationUnavailable;

    /// <summary>Windows Hello で確かめる(使えなければ Windows のパスワード)</summary>
    public bool HelloAvailable => fakeAuthentication || Hello is HelloAvailability.Available or HelloAvailability.DeviceBusy;

    /// <summary>設定の変更と終了に認証が要るか</summary>
    public bool NeedsSelfAuthentication => Store.Current.ProtectSelf && Store.Current.Apps.Count > 0 && !authenticationUnavailable;

    public bool StartupEnabled
    {
        get => !Testing.Enabled && StartupRegistration.IsEnabled;
        set
        {
            if (Testing.Enabled || Environment.ProcessPath is not { } exe) return;
            if (value) StartupRegistration.Enable(exe);
            else StartupRegistration.Disable();
            Log.Write(value ? "サインイン時に開始: オン" : "サインイン時に開始: オフ");
        }
    }

    // ---- 操作 ----

    public void ShowMainWindow()
    {
        window ??= new MainWindow(this);
        window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
        var handle = new WindowInteropHelper(window).Handle;
        if (Win32.GetForegroundWindow() != handle) PromptOwner.BringToFront(handle);
    }

    public void MainWindowClosed()
    {
        if (Store.Current.ToldAboutTray) return;
        tray.ShowNotification("アプリロックは動き続けています", "通知領域のアイコンから、いつでも設定を開けます。");
        Store.Update(settings => settings.ToldAboutTray = true);
    }

    public async Task<bool> AuthenticateAsync(string reason)
    {
        var result = await authenticator.AuthenticateAsync(reason);
        if (result == AuthResult.Unavailable && !authenticationUnavailable)
        {
            authenticationUnavailable = true;
            StateChanged?.Invoke();
        }
        return result == AuthResult.Verified;
    }

    public void RelockAll() => Coordinator.RelockAll();

    public async Task QuitAsync()
    {
        if (NeedsSelfAuthentication && !await AuthenticateAsync("アプリロックを終了")) return;
        Log.Write("終了を選んだ");
        application.Shutdown();
    }

    /// <summary>アプリを開く(ロック中なら、起動したところで認証を求める)</summary>
    public void OpenApp(LockedApp app)
    {
        try
        {
            if (app.PackageFamilyName is not null && app.AppUserModelId is not null)
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $@"shell:AppsFolder\{app.AppUserModelId}") { UseShellExecute = true });
            }
            else if (app.Path is { } path && File.Exists(path))
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = System.IO.Path.GetDirectoryName(path) });
            }
            else
            {
                MessageBox.Show($"「{app.Name}」が見つかりませんでした。", "アプリロック", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception e)
        {
            Log.Write($"開けなかった: {app.Id}: {e.Message}");
            MessageBox.Show($"「{app.Name}」を開けませんでした。", "アプリロック", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public static void OpenUri(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            Log.Write($"開けなかった: {uri}: {e.Message}");
        }
    }

    public async Task RefreshHelloAsync()
    {
        HelloAvailability? next;
        if (fakeAuthentication)
        {
            next = HelloAvailability.Available;
        }
        else
        {
            try
            {
                next = await WinRtHello.CheckAvailabilityAsync();
            }
            catch (Exception e)
            {
                Log.Write($"Windows Hello の状態を読めない: {e.Message}");
                next = null;
            }
        }
        if (next == Hello) return;
        Hello = next;
        Log.Write($"Windows Hello: {next?.ToString() ?? "不明"}");
        StateChanged?.Invoke();
    }

    // ---- できごと ----

    private void PublishPolicy() => watcher.UpdatePolicy(Store.Current.Apps, Coordinator.UnlockedIds(), Coordinator.RevealedIds());

    private void OnCoordinatorChanged()
    {
        PublishPolicy();
        UpdateTooltip();
        StateChanged?.Invoke();
    }

    private void OnSettingsChanged()
    {
        PublishPolicy();
        Coordinator.SettingsChanged();
        PublishPolicy();
        StateChanged?.Invoke();
    }

    private void Tick()
    {
        Coordinator.Sweep();
        PublishPolicy();
        window?.Tick();
    }

    private void OnAuthenticationFailed(string id, AuthResult result)
    {
        if (result != AuthResult.Unavailable) return;
        authenticationUnavailable = true;
        var name = Store.Current.Find(id)?.Name ?? id;
        tray.ShowNotification($"「{name}」を開けませんでした",
            "本人確認の方法がありません。Windows のサインインにパスワードか PIN を設定してください。", warning: true);
        _ = RefreshHelloAsync();
        StateChanged?.Invoke();
    }

    private readonly HashSet<string> toldNotLockable = new(StringComparer.OrdinalIgnoreCase);

    private void OnAppNotLockable(string exeName)
    {
        if (!toldNotLockable.Add(exeName)) return;
        var name = Store.Current.Apps.FirstOrDefault(a => string.Equals(a.ExeName, exeName, StringComparison.OrdinalIgnoreCase))?.Name ?? exeName;
        tray.ShowNotification($"「{name}」をロックできません", "管理者として実行しているアプリは、ロックできません。", warning: true);
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionLock) dispatcher.BeginInvoke(Coordinator.SessionLocked);
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) dispatcher.BeginInvoke(Coordinator.SessionLocked);
    }

    private void OnSessionEnding(object sender, SessionEndingEventArgs e)
    {
        // サインアウト・シャットダウンの前に、止めたアプリを動かす(止めたままだと終わるのを待たせる)
        Log.Write($"Windows の終了・サインアウト: {e.Reason}");
        watcher.ReleaseAll();
    }

    private void UpdateTooltip()
    {
        var unlocked = Store.Current.Apps.Count(a => Coordinator.IsUnlocked(a.Id));
        tray.SetTooltip(unlocked > 0 ? $"アプリロック・{unlocked} 個のアプリを解除中" : "アプリロック");
    }

    private static byte[] LoadResource(string path)
    {
        var info = Application.GetResourceStream(new Uri($"pack://application:,,,/{path}"))
                   ?? throw new InvalidOperationException($"{path} がありません");
        using var stream = info.Stream;
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    /// <summary>失敗して終わるときに、止めたアプリを残さない</summary>
    public void EmergencyRelease()
    {
        try
        {
            watcher.ReleaseAll();
        }
        catch
        {
            // 終わる途中なので何もしない
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        timer.Stop();
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionEnding -= OnSessionEnding;
        watcher.ReleaseAll();
        watcher.Dispose();
        tray.Dispose();
        Log.Write("終了");
    }
}

/// <summary>通知領域のアイコンを右クリックしたときのメニュー</summary>
internal sealed class TrayMenu
{
    private readonly AppController app;
    private readonly Window host;
    private readonly ContextMenu menu;
    private readonly MenuItem relockItem;

    public TrayMenu(AppController app)
    {
        this.app = app;
        // メニューの持ち主にする見えない窓(前に出しておかないと、ほかを押してもメニューが閉じない)
        host = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = true,
            ResizeMode = ResizeMode.NoResize,
            Width = 1,
            Height = 1,
            Left = -32000,
            Top = -32000,
            Topmost = true,
        };
        menu = new ContextMenu { Placement = PlacementMode.MousePoint, Language = System.Windows.Markup.XmlLanguage.GetLanguage("ja-JP") };
        var open = new MenuItem { Header = "アプリロックを開く", FontWeight = FontWeights.SemiBold };
        open.Click += (_, _) => app.ShowMainWindow();
        relockItem = new MenuItem { Header = "すべてロック" };
        relockItem.Click += (_, _) => app.RelockAll();
        var quit = new MenuItem { Header = "終了" };
        quit.Click += async (_, _) => await app.QuitAsync();
        menu.Items.Add(open);
        menu.Items.Add(relockItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(quit);
        menu.Closed += (_, _) => host.Hide();
    }

    public void Show()
    {
        relockItem.IsEnabled = app.Coordinator.HasUnlockedApps;
        host.Show();
        var handle = new WindowInteropHelper(host).Handle;
        Win32.SetForegroundWindow(handle);
        menu.PlacementTarget = host;
        menu.IsOpen = true;
    }
}

/// <summary>シェルの部品(アイコン・ショートカット)を使う STA のスレッド</summary>
internal static class StaWorker
{
    private static readonly System.Collections.Concurrent.BlockingCollection<Action> Queue = new();
    private static readonly object Gate = new();
    private static Thread? thread;

    public static Task<T> Run<T>(Func<T> work)
    {
        lock (Gate)
        {
            if (thread is null)
            {
                thread = new Thread(() =>
                {
                    foreach (var action in Queue.GetConsumingEnumerable()) action();
                })
                { IsBackground = true, Name = "AppLock shell" };
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
            }
        }
        var source = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Queue.Add(() =>
        {
            try
            {
                source.SetResult(work());
            }
            catch (Exception e)
            {
                source.SetException(e);
            }
        });
        return source.Task;
    }
}

/// <summary>アプリのアイコン(一度読んだものは覚えておく)</summary>
internal sealed class IconCache
{
    private readonly Dictionary<string, Task<ImageSource?>> cache = new(StringComparer.OrdinalIgnoreCase);

    public static string? ParsingName(LockedApp app) =>
        app.PackageFamilyName is not null && app.AppUserModelId is not null ? $@"shell:AppsFolder\{app.AppUserModelId}" : app.Path;

    public Task<ImageSource?> GetAsync(string? parsingName)
    {
        if (string.IsNullOrWhiteSpace(parsingName)) return Task.FromResult<ImageSource?>(null);
        if (!cache.TryGetValue(parsingName, out var task))
        {
            cache[parsingName] = task = StaWorker.Run<ImageSource?>(() => ShellItems.Icon(parsingName, 64));
        }
        return task;
    }
}
