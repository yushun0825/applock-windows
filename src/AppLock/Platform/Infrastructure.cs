using System.IO;
using System.Text;
using AppLock.Core;
using Microsoft.Win32;

namespace AppLock.Platform;

/// <summary>設定・記録・止めた記録の置き場所(%LOCALAPPDATA%\AppLock)</summary>
internal static class Paths
{
    public static string DataDirectory =>
        Testing.DataDirectory ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Testing.Enabled ? "AppLock.Testing" : "AppLock");
}

/// <summary>記録(%LOCALAPPDATA%\AppLock\logs\applock.log・1 MB で 1 つ前の分と入れ替え)</summary>
internal static class Log
{
    private const long MaxBytes = 1 << 20;
    private static readonly object Gate = new();
    private static string? path;
    private static StreamWriter? writer;

    public static void Init(string directory)
    {
        var folder = System.IO.Path.Combine(directory, "logs");
        Directory.CreateDirectory(folder);
        path = System.IO.Path.Combine(folder, "applock.log");
        Open();
    }

    public static void Write(string line)
    {
        lock (Gate)
        {
            if (writer is null) return;
            try
            {
                if (writer.BaseStream.Length > MaxBytes)
                {
                    writer.Dispose();
                    File.Move(path!, System.IO.Path.ChangeExtension(path!, ".1.log"), overwrite: true);
                    Open();
                }
                writer!.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {line}");
            }
            catch
            {
                // 記録が書けなくても止まらない
            }
        }
    }

    private static void Open()
    {
        var stream = new FileStream(path!, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
    }
}

/// <summary>設定を読む・保存する。公開した設定は変えず、変えるときは写してから差し替える。</summary>
internal sealed class SettingsStore
{
    private readonly string directory;
    private readonly Action<string> log;

    public SettingsStore(string directory, Action<string> log)
    {
        this.directory = directory;
        this.log = log;
        ExistedAtStart = File.Exists(FilePath);
        Current = Load();
    }

    public event Action? Changed;

    public LockSettings Current { get; private set; }

    /// <summary>起動したときに設定があった(無ければ初めての起動)</summary>
    public bool ExistedAtStart { get; }

    private string FilePath => System.IO.Path.Combine(directory, "settings.json");

    public void Update(Action<LockSettings> change)
    {
        var next = Current.Clone();
        change(next);
        next.Normalize();
        Save(next);
        Current = next;
        Changed?.Invoke();
    }

    public void Save(LockSettings? settings = null)
    {
        settings ??= Current;
        try
        {
            Directory.CreateDirectory(directory);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, SettingsJson.Serialize(settings), new UTF8Encoding(false));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception e)
        {
            log($"設定を保存できない: {e.Message}");
        }
    }

    private LockSettings Load()
    {
        if (!File.Exists(FilePath)) return new LockSettings();
        try
        {
            return SettingsJson.Parse(File.ReadAllText(FilePath));
        }
        catch (Exception e)
        {
            // 読めない設定は控えに移し、既定の値で始める
            log($"設定を読めない(既定の値で始める): {e.Message}");
            try
            {
                File.Copy(FilePath, FilePath + ".bad", overwrite: true);
            }
            catch
            {
                // 控えが作れなくても続ける
            }
            return new LockSettings();
        }
    }
}

/// <summary>サインインしたときに開く(HKCU の Run)。タスク マネージャーで無効にしたものも見る。</summary>
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "AppLock";

    public static string Command(string exePath) => $"\"{exePath}\" --background";

    public static bool IsEnabled
    {
        get
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey);
            if (run?.GetValue(ValueName) is not string) return false;
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            // 先頭のバイトが奇数なら、タスク マネージャーで無効にしてある
            return approved?.GetValue(ValueName) is not byte[] { Length: > 0 } flags || (flags[0] & 1) == 0;
        }
    }

    public static void Enable(string exePath)
    {
        using (var run = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            run.SetValue(ValueName, Command(exePath));
        }
        using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
        if (approved?.GetValue(ValueName) is byte[] { Length: > 0 } flags && (flags[0] & 1) == 1)
        {
            var enabled = (byte[])flags.Clone();
            enabled[0] = 2;
            approved.SetValue(ValueName, enabled, RegistryValueKind.Binary);
        }
    }

    public static void Disable()
    {
        using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        run?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>登録してある場所が今の実行ファイルと違えば書き直す(入れ直して場所が変わったとき)</summary>
    public static void Refresh(string exePath)
    {
        using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (run?.GetValue(ValueName) is string command && command != Command(exePath)) run.SetValue(ValueName, Command(exePath));
    }
}

/// <summary>同時に 1 つだけ動かす。2 つ目は、1 つ目に「窓を出す」か「終了」を頼んで終わる。</summary>
internal sealed class SingleInstance : IDisposable
{
    private static string Suffix => Testing.Enabled ? ".Testing" : "";
    private static string ShowName => $@"Local\AppLock.Show{Suffix}";
    private static string QuitName => $@"Local\AppLock.Quit{Suffix}";

    private readonly Mutex mutex;
    private readonly List<RegisteredWaitHandle> waits = [];
    private EventWaitHandle? showEvent;
    private EventWaitHandle? quitEvent;

    public SingleInstance()
    {
        mutex = new Mutex(true, $@"Local\AppLock.Instance{Suffix}", out var created);
        IsFirst = created;
    }

    public bool IsFirst { get; }

    public void Listen(Action onShow, Action onQuit)
    {
        showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowName);
        quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, QuitName);
        waits.Add(ThreadPool.RegisterWaitForSingleObject(showEvent, (_, _) => onShow(), null, Timeout.Infinite, false));
        waits.Add(ThreadPool.RegisterWaitForSingleObject(quitEvent, (_, _) => onQuit(), null, Timeout.Infinite, false));
    }

    public static bool Signal(bool quit)
    {
        try
        {
            using var handle = EventWaitHandle.OpenExisting(quit ? QuitName : ShowName);
            return handle.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        foreach (var wait in waits) wait.Unregister(null);
        showEvent?.Dispose();
        quitEvent?.Dispose();
        if (IsFirst) mutex.ReleaseMutex();
        mutex.Dispose();
    }
}
