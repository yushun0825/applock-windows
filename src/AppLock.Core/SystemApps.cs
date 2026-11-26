namespace AppLock.Core;

/// <summary>Windows そのものの部品。ロックの対象にしない・切り替えとして数えない。</summary>
public static class SystemApps
{
    /// <summary>ロックすると Windows を操作できなくなるもの・ロックしても意味をなさないもの</summary>
    private static readonly HashSet<string> NotLockable = new(StringComparer.OrdinalIgnoreCase)
    {
        "AppLock.exe",
        "explorer.exe", "dwm.exe", "csrss.exe", "winlogon.exe", "LogonUI.exe", "LockApp.exe", "consent.exe",
        "CredentialUIBroker.exe", "ShellExperienceHost.exe", "StartMenuExperienceHost.exe", "ShellHost.exe",
        "SearchHost.exe", "SearchApp.exe", "SearchUI.exe", "TextInputHost.exe", "ApplicationFrameHost.exe",
        "sihost.exe", "ctfmon.exe", "svchost.exe", "conhost.exe", "OpenConsole.exe", "RuntimeBroker.exe",
        "dllhost.exe", "rundll32.exe", "taskhostw.exe", "smartscreen.exe", "SecurityHealthSystray.exe",
        "SystemSettings.exe", "SystemSettingsBroker.exe", "Taskmgr.exe", "WWAHost.exe", "backgroundTaskHost.exe",
        "fontdrvhost.exe", "WUDFHost.exe", "audiodg.exe", "msiexec.exe", "MsMpEng.exe", "SecHealthUI.exe",
        "cmd.exe", "powershell.exe", "pwsh.exe",
    };

    /// <summary>前に出ても「ほかのアプリに切り替えた」と見なさないもの(認証の窓・スタート・検索・タスク バーなど)</summary>
    private static readonly HashSet<string> IgnoredForSwitching = new(StringComparer.OrdinalIgnoreCase)
    {
        "AppLock.exe", "CredentialUIBroker.exe", "consent.exe", "LockApp.exe", "LogonUI.exe",
        "ShellExperienceHost.exe", "StartMenuExperienceHost.exe", "ShellHost.exe", "SearchHost.exe",
        "SearchApp.exe", "TextInputHost.exe",
    };

    /// <summary>エクスプローラーの窓のうち、タスク バー・通知領域・タスクの切り替えのもの</summary>
    private static readonly HashSet<string> ShellWindowClasses = new(StringComparer.Ordinal)
    {
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow", "TopLevelWindowForOverflowXamlIsland",
        "XamlExplorerHostIslandWindow", "MultitaskingViewFrame", "TaskSwitcherWnd", "ForegroundStaging",
    };

    public static bool CanLock(string exeName) => !string.IsNullOrWhiteSpace(exeName) && !NotLockable.Contains(exeName);

    public static bool IsIgnoredForSwitching(string exeName, string? windowClass) =>
        IgnoredForSwitching.Contains(exeName) ||
        (string.Equals(exeName, "explorer.exe", StringComparison.OrdinalIgnoreCase) && windowClass != null && ShellWindowClasses.Contains(windowClass));

    /// <summary>アンインストーラーらしい名前(スタート メニューの一覧から外す)</summary>
    public static bool LooksLikeUninstaller(string name) =>
        name.Contains("uninst", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("アンインストール", StringComparison.Ordinal);
}
