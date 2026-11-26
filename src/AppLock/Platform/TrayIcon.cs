using System.Runtime.InteropServices;
using System.Windows.Interop;
using static AppLock.Native.Win32;

namespace AppLock.Platform;

/// <summary>通知領域のアイコン(Shell_NotifyIcon を直接使う)。</summary>
internal sealed unsafe class TrayIcon : IDisposable
{
    private const int WM_TRAY = 0x8000 + 1;
    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    private const uint NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_INFO = 0x10, NIF_SHOWTIP = 0x80;
    private const uint NOTIFYICON_VERSION_4 = 4;
    private const int NIN_SELECT = 0x400, NIN_KEYSELECT = 0x401, WM_CONTEXTMENU = 0x007B;
    private const uint NIIF_INFO = 0x1, NIIF_WARNING = 0x2, NIIF_RESPECT_QUIET_TIME = 0x80;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        public fixed char szTip[128];
        public uint dwState;
        public uint dwStateMask;
        public fixed char szInfo[256];
        public uint uVersion;
        public fixed char szInfoTitle[64];
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll")]
    private static extern bool Shell_NotifyIconW(uint message, NOTIFYICONDATAW* data);

    private readonly HwndSource window;
    private readonly uint taskbarCreated;
    private readonly byte[] iconFile;
    private IntPtr icon;
    private string tooltip;
    private bool added;

    public TrayIcon(byte[] iconFile, string tooltip)
    {
        this.iconFile = iconFile;
        this.tooltip = tooltip;
        taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
        // エクスプローラーが作り直されたときの知らせ(TaskbarCreated)を受けるため、見えない最上位の窓にする
        window = new HwndSource(new HwndSourceParameters("AppLockTray") { WindowStyle = unchecked((int)0x80000000), Width = 0, Height = 0 });
        window.AddHook(WndProc);
        icon = LoadIcon();
        Add();
    }

    public event Action? LeftClick;
    public event Action<int, int>? RightClick;

    public void SetTooltip(string text)
    {
        if (text == tooltip) return;
        tooltip = text;
        var data = Data(NIF_TIP | NIF_SHOWTIP);
        Shell_NotifyIconW(NIM_MODIFY, &data);
    }

    /// <summary>通知を出す(Windows の通知として出る)</summary>
    public void ShowNotification(string title, string text, bool warning = false)
    {
        var data = Data(NIF_INFO);
        Copy(title, data.szInfoTitle, 64);
        Copy(text, data.szInfo, 256);
        data.dwInfoFlags = (warning ? NIIF_WARNING : NIIF_INFO) | NIIF_RESPECT_QUIET_TIME;
        Shell_NotifyIconW(NIM_MODIFY, &data);
    }

    public void Dispose()
    {
        if (added)
        {
            var data = Data(0);
            Shell_NotifyIconW(NIM_DELETE, &data);
            added = false;
        }
        if (icon != IntPtr.Zero) DestroyIcon(icon);
        window.Dispose();
    }

    private void Add()
    {
        var data = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        added = Shell_NotifyIconW(NIM_ADD, &data);
        if (!added) Log.Write("通知領域にアイコンを出せない");
        data.uVersion = NOTIFYICON_VERSION_4;
        Shell_NotifyIconW(NIM_SETVERSION, &data);
    }

    private NOTIFYICONDATAW Data(uint flags)
    {
        var data = new NOTIFYICONDATAW
        {
            cbSize = (uint)sizeof(NOTIFYICONDATAW),
            hWnd = window.Handle,
            uID = 1,
            uFlags = flags,
            uCallbackMessage = WM_TRAY,
            hIcon = icon,
        };
        Copy(tooltip, data.szTip, 128);
        return data;
    }

    private static void Copy(string text, char* destination, int capacity)
    {
        var length = Math.Min(text.Length, capacity - 1);
        for (var i = 0; i < length; i++) destination[i] = text[i];
        destination[length] = '\0';
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_TRAY)
        {
            var notification = (int)((long)lParam & 0xFFFF);
            switch (notification)
            {
                case NIN_SELECT:
                case NIN_KEYSELECT:
                    LeftClick?.Invoke();
                    handled = true;
                    break;
                case WM_CONTEXTMENU:
                    var x = (short)((long)wParam & 0xFFFF);
                    var y = (short)(((long)wParam >> 16) & 0xFFFF);
                    RightClick?.Invoke(x, y);
                    handled = true;
                    break;
            }
        }
        else if (msg == (int)taskbarCreated)
        {
            // エクスプローラーが作り直された(アイコンは消えている)
            added = false;
            if (icon != IntPtr.Zero) DestroyIcon(icon);
            icon = LoadIcon();
            Add();
        }
        return IntPtr.Zero;
    }

    /// <summary>.ico の中から、通知領域の大きさに合うものを選んでアイコンにする</summary>
    private IntPtr LoadIcon()
    {
        var size = GetSystemMetricsForDpi(SM_CXSMICON, GetDpiForSystem());
        if (size <= 0) size = 16;
        var count = BitConverter.ToUInt16(iconFile, 4);
        int bestOffset = -1, bestLength = 0, bestSize = int.MaxValue;
        for (var i = 0; i < count; i++)
        {
            var entry = 6 + i * 16;
            var width = iconFile[entry] == 0 ? 256 : iconFile[entry];
            var length = BitConverter.ToInt32(iconFile, entry + 8);
            var offset = BitConverter.ToInt32(iconFile, entry + 12);
            // ちょうど・それより大きい中でいちばん小さいもの
            var score = width >= size ? width - size : 1000 + size - width;
            if (score < bestSize)
            {
                bestSize = score;
                bestOffset = offset;
                bestLength = length;
            }
        }
        if (bestOffset < 0) return IntPtr.Zero;
        fixed (byte* bits = &iconFile[bestOffset])
        {
            return CreateIconFromResourceEx(bits, (uint)bestLength, true, 0x00030000, size, size, 0);
        }
    }
}
