# 指定したプロセスの窓が画面に出ていたかを、約 2 ms ごとに数える(アプリロックの確かめ用)。
# 結果は英数字だけで書く: 見えていた回数・最初と最後に見えた時刻・変化の記録。
param(
    [Parameter(Mandatory)] [string[]] $Names,
    [double] $Seconds = 6,
    [Parameter(Mandatory)] [string] $Out
)
$ErrorActionPreference = 'Stop'
$Names = @($Names | ForEach-Object { $_ -split ',' })

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

public static class WindowWatch
{
    delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
    [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint period);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }

    // 画面に出ていて、隠れても縮んでもおらず、40x40 以上の窓の数
    static int CountVisible(HashSet<string> names)
    {
        var pids = new HashSet<uint>();
        foreach (var name in names)
            foreach (var p in Process.GetProcessesByName(name)) { pids.Add((uint)p.Id); p.Dispose(); }
        int count = 0;
        EnumWindows((hwnd, l) =>
        {
            uint pid; GetWindowThreadProcessId(hwnd, out pid);
            if (!pids.Contains(pid) || !IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;
            int cloaked; if (DwmGetWindowAttribute(hwnd, 14, out cloaked, 4) == 0 && cloaked != 0) return true;
            RECT r; GetWindowRect(hwnd, out r);
            if (r.Right - r.Left >= 40 && r.Bottom - r.Top >= 40) count++;
            return true;
        }, IntPtr.Zero);
        return count;
    }

    public static string Run(string[] names, double seconds)
    {
        timeBeginPeriod(1);
        var set = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        var log = new StringBuilder();
        var watch = Stopwatch.StartNew();
        int samples = 0, visibleSamples = 0, last = -1;
        string first = "-", lastSeen = "-";
        while (watch.Elapsed.TotalSeconds < seconds)
        {
            int n = CountVisible(set);
            samples++;
            if (n > 0)
            {
                visibleSamples++;
                lastSeen = DateTime.Now.ToString("HH:mm:ss.fff");
                if (first == "-") first = lastSeen;
            }
            if (n != last)
            {
                log.AppendLine(DateTime.Now.ToString("HH:mm:ss.fff") + " visible=" + n);
                last = n;
            }
            Thread.Sleep(1);
        }
        return "samples=" + samples + " visible_samples=" + visibleSamples + " first_visible=" + first + " last_visible=" + lastSeen + "\r\n" + log;
    }
}
'@

Set-Content -Path "$Out.ready" -Value 'ready' -Encoding ASCII
$result = [WindowWatch]::Run($Names, $Seconds)
Set-Content -Path $Out -Value $result -Encoding ASCII
