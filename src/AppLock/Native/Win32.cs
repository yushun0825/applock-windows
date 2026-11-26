using System.Runtime.InteropServices;

namespace AppLock.Native;

/// <summary>Windows の API(user32・kernel32・ntdll・dwmapi)。</summary>
internal static unsafe class Win32
{
    // ---- 窓のできごと(WinEvent) ----
    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint EVENT_OBJECT_CREATE = 0x8000;
    public const uint EVENT_OBJECT_SHOW = 0x8002;
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    public const int OBJID_WINDOW = 0;
    public const int CHILDID_SELF = 0;

    public delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")]
    public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc proc, uint process, uint thread, uint flags);

    [DllImport("user32.dll")]
    public static extern bool UnhookWinEvent(IntPtr hook);

    // ---- 知らせの列 ----
    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int x;
        public int y;
    }

    public const uint WM_QUIT = 0x0012;
    public const uint WM_TIMER = 0x0113;

    [DllImport("user32.dll")]
    public static extern int GetMessageW(out MSG msg, IntPtr hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    public static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll")]
    public static extern IntPtr DispatchMessageW(ref MSG msg);

    [DllImport("user32.dll")]
    public static extern bool PostThreadMessageW(uint thread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern UIntPtr SetTimer(IntPtr hwnd, UIntPtr id, uint elapse, IntPtr proc);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    // ---- 窓 ----
    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    public const uint GA_ROOT = 2;

    [DllImport("user32.dll")]
    public static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    public const uint GW_OWNER = 4;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;
    public const long WS_CHILD = 0x40000000;
    public const long WS_EX_TOOLWINDOW = 0x00000080;
    public const long WS_EX_APPWINDOW = 0x00040000;
    public const long WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll")]
    public static extern bool ShowWindowAsync(IntPtr hwnd, int cmd);
    public const int SW_HIDE = 0;
    public const int SW_SHOWNA = 8;

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern bool AllowSetForegroundWindow(uint pid);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassNameW(IntPtr hwnd, char* name, int max);

    public static string ClassName(IntPtr hwnd)
    {
        var buffer = stackalloc char[256];
        var length = GetClassNameW(hwnd, buffer, 256);
        return length > 0 ? new string(buffer, 0, length) : "";
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X, Y;
    }

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT point);

    // ---- 入力(前に出す許しを得るため) ----
    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public INPUTUNION u;
    }

    public const uint INPUT_KEYBOARD = 1;
    public const uint KEYEVENTF_KEYUP = 0x0002;

    [DllImport("user32.dll")]
    public static extern uint SendInput(uint count, INPUT[] inputs, int size);

    // ---- プロセス ----
    public const uint PROCESS_TERMINATE = 0x0001;
    public const uint PROCESS_SUSPEND_RESUME = 0x0800;
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint SYNCHRONIZE = 0x00100000;
    public const uint WAIT_OBJECT_0 = 0;
    public const uint WAIT_TIMEOUT = 0x102;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll")]
    public static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, char* name, ref uint size);

    public static string? ImagePath(IntPtr process)
    {
        var buffer = stackalloc char[1024];
        uint size = 1024;
        return QueryFullProcessImageNameW(process, 0, buffer, ref size) ? new string(buffer, 0, (int)size) : null;
    }

    [DllImport("kernel32.dll")]
    public static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll")]
    public static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll")]
    public static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll")]
    public static extern bool ProcessIdToSessionId(uint pid, out uint session);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetPackageFamilyName(IntPtr process, ref uint length, char* name);

    public static string? PackageFamilyName(IntPtr process)
    {
        var buffer = stackalloc char[256];
        uint length = 256;
        // 15700 = APPMODEL_ERROR_NO_PACKAGE(パッケージのアプリではない)
        return GetPackageFamilyName(process, ref length, buffer) == 0 && length > 1 ? new string(buffer, 0, (int)length - 1) : null;
    }

    [DllImport("ntdll.dll")]
    public static extern int NtSuspendProcess(IntPtr process);

    [DllImport("ntdll.dll")]
    public static extern int NtResumeProcess(IntPtr process);

    public const int SystemProcessInformation = 5;
    public const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);

    [DllImport("ntdll.dll")]
    public static extern int NtQuerySystemInformation(int infoClass, void* buffer, int length, out int returnLength);

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll")]
    public static extern int NtQueryInformationProcess(IntPtr process, int infoClass, out PROCESS_BASIC_INFORMATION info, int length, out int returnLength);

    // ---- DWM ----
    public const int DWMWA_CLOAKED = 14;

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    // ---- 窓を作る(認証の窓の持ち主・通知領域) ----
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);
    public const uint LWA_ALPHA = 0x2;

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hwnd, int cmd);
    public const int SW_SHOW = 5;

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(POINT point, uint flags);
    public const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    public static extern bool GetMonitorInfoW(IntPtr monitor, ref MONITORINFO info);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForSystem();

    [DllImport("user32.dll")]
    public static extern int GetSystemMetricsForDpi(int index, uint dpi);
    public const int SM_CXSMICON = 49;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr CreateIconFromResourceEx(byte* bits, uint size, bool icon, uint version, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern uint RegisterWindowMessageW(string name);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(IntPtr obj);
}
