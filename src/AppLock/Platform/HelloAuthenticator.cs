using System.Runtime.InteropServices;
using System.Windows.Interop;
using AppLock.Core;
using AppLock.Native;
using static AppLock.Native.Win32;

namespace AppLock.Platform;

/// <summary>本人か確かめる。Windows Hello(PIN・指紋・顔)を使い、使えない PC では Windows のパスワードで確かめる。</summary>
internal sealed class HelloAuthenticator(Action<string> log) : IAuthenticator
{
    public async Task<AuthResult> AuthenticateAsync(string message)
    {
        HelloAvailability? availability;
        try
        {
            availability = await WinRtHello.CheckAvailabilityAsync();
        }
        catch (Exception e)
        {
            log($"Windows Hello の状態を読めない: {e.Message}");
            availability = null;
        }

        // 持ち主の窓は、確認のあと少し残す(解除したアプリを前に出す許しを、この窓が前にいるうちに渡すため)
        var owner = new PromptOwner();
        try
        {
            if (availability is HelloAvailability.Available or HelloAvailability.DeviceBusy)
            {
                HelloResult result;
                try
                {
                    result = await WinRtHello.RequestVerificationForWindowAsync(owner.Handle, message);
                }
                catch (Exception e)
                {
                    log($"Windows Hello の確認を出せない: {e.Message}");
                    return AuthResult.Failed;
                }
                log($"Windows Hello: {result}");
                switch (result)
                {
                    case HelloResult.Verified:
                        return AuthResult.Verified;
                    case HelloResult.Canceled:
                        return AuthResult.Canceled;
                    case HelloResult.DeviceBusy or HelloResult.RetriesExhausted:
                        return AuthResult.Failed;
                }
                // 確かめる途中で Windows Hello が使えなくなった
            }
            else
            {
                log($"Windows Hello を使えないので、パスワードで確かめる: {availability?.ToString() ?? "不明"}");
            }
            PromptOwner.BringToFront(owner.Handle);
            var password = WindowsPassword.Verify(owner.Handle, message, log);
            log($"パスワード: {password}");
            return password;
        }
        finally
        {
            owner.DisposeAfter(TimeSpan.FromSeconds(1.5));
        }
    }
}

/// <summary>
/// 確認の窓の持ち主にする、ほぼ透明な 1×1 の窓。マウスのある画面の真ん中に置いて前に出す。
/// (通知領域で動くアプリには前に出せる窓が無く、確認の窓がほかの窓の後ろに出てしまうため)
/// </summary>
internal sealed class PromptOwner : IDisposable
{
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    private readonly HwndSource source;

    public PromptOwner()
    {
        GetCursorPos(out var cursor);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfoW(MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST), ref info);
        var x = (info.rcWork.Left + info.rcWork.Right) / 2;
        var y = (info.rcWork.Top + info.rcWork.Bottom) / 2;

        source = new HwndSource(new HwndSourceParameters("アプリロック")
        {
            WindowStyle = WS_POPUP,
            ExtendedWindowStyle = WS_EX_TOPMOST | WS_EX_LAYERED | WS_EX_TOOLWINDOW,
            PositionX = x,
            PositionY = y,
            Width = 1,
            Height = 1,
        });
        SetLayeredWindowAttributes(source.Handle, 0, 1, LWA_ALPHA);
        SetWindowPos(source.Handle, HWND_TOPMOST, x, y, 1, 1, SWP_SHOWWINDOW);
        BringToFront(source.Handle);
    }

    public IntPtr Handle => source.Handle;

    /// <summary>前に出す。Windows は最後に入力を受けたプロセスにしか前に出ることを許さないので、
    /// 何もしないキー(割り当ての無い仮想キー 0xE8)を送ってから前に出す。</summary>
    public static void BringToFront(IntPtr hwnd)
    {
        var inputs = new INPUT[2];
        inputs[0].type = INPUT_KEYBOARD;
        inputs[0].u.ki.wVk = 0xE8;
        inputs[1].type = INPUT_KEYBOARD;
        inputs[1].u.ki.wVk = 0xE8;
        inputs[1].u.ki.dwFlags = KEYEVENTF_KEYUP;
        SendInput(2, inputs, Marshal.SizeOf<INPUT>());
        SetForegroundWindow(hwnd);
    }

    public void DisposeAfter(TimeSpan delay)
    {
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Dispose();
        };
        timer.Start();
    }

    public void Dispose() => source.Dispose();
}
