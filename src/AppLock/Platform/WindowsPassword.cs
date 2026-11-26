using System.Runtime.InteropServices;
using System.Security.Principal;
using AppLock.Core;

namespace AppLock.Platform;

/// <summary>
/// Windows Hello が使えない PC では、Windows のサインインのパスワードで確かめる(Windows の「資格情報」の窓)。
/// 入力されたパスワードはその場で確かめるだけで、保存しない。確かめたあとはメモリから消す。
/// </summary>
internal static unsafe class WindowsPassword
{
    private const int CREDUIWIN_ENUMERATE_CURRENT_USER = 0x200;
    private const int ERROR_CANCELLED = 1223;
    private const int ERROR_LOGON_FAILURE = 1326;
    private const int ERROR_ACCOUNT_RESTRICTION = 1327;
    private const int LOGON32_LOGON_INTERACTIVE = 2;
    private const int LOGON32_PROVIDER_DEFAULT = 0;
    private const int MaxLength = 514;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDUI_INFOW
    {
        public int cbSize;
        public IntPtr hwndParent;
        public string pszMessageText;
        public string pszCaptionText;
        public IntPtr hbmBanner;
    }

    [DllImport("credui.dll", CharSet = CharSet.Unicode)]
    private static extern int CredUIPromptForWindowsCredentialsW(ref CREDUI_INFOW info, int authError, ref uint authPackage,
        IntPtr inBuffer, uint inSize, out IntPtr outBuffer, out uint outSize, ref bool save, int flags);

    [DllImport("credui.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredUnPackAuthenticationBufferW(int flags, IntPtr buffer, uint size,
        char* user, ref int userLength, char* domain, ref int domainLength, char* password, ref int passwordLength);

    [DllImport("credui.dll", CharSet = CharSet.Unicode)]
    private static extern int CredUIParseUserNameW(string userName, char* user, int userMax, char* domain, int domainMax);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LogonUserW(string user, string domain, char* password, int logonType, int provider, out IntPtr token);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>今サインインしている人のパスワードか確かめる。間違えたら 5 回まで聞き直す。</summary>
    public static AuthResult Verify(IntPtr owner, string message, Action<string> log)
    {
        var currentUser = WindowsIdentity.GetCurrent().User;
        var error = 0;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var info = new CREDUI_INFOW
            {
                cbSize = Marshal.SizeOf<CREDUI_INFOW>(),
                hwndParent = owner,
                pszCaptionText = "アプリロック",
                pszMessageText = message,
            };
            uint package = 0;
            var save = false;
            var status = CredUIPromptForWindowsCredentialsW(ref info, error, ref package, IntPtr.Zero, 0,
                out var buffer, out var size, ref save, CREDUIWIN_ENUMERATE_CURRENT_USER);
            if (status == ERROR_CANCELLED) return AuthResult.Canceled;
            if (status != 0)
            {
                log($"パスワードの確認を出せない: {status}");
                return AuthResult.Failed;
            }
            try
            {
                var result = Check(buffer, size, currentUser, log);
                if (result != AuthResult.Failed) return result;
                error = ERROR_LOGON_FAILURE;
            }
            finally
            {
                new Span<byte>((void*)buffer, (int)size).Clear();
                Marshal.FreeCoTaskMem(buffer);
            }
        }
        return AuthResult.Failed;
    }

    private static AuthResult Check(IntPtr buffer, uint size, SecurityIdentifier? currentUser, Action<string> log)
    {
        var user = stackalloc char[MaxLength];
        var domain = stackalloc char[MaxLength];
        var password = stackalloc char[MaxLength];
        var account = stackalloc char[MaxLength];
        var accountDomain = stackalloc char[MaxLength];
        try
        {
            int userLength = MaxLength, domainLength = MaxLength, passwordLength = MaxLength;
            if (!CredUnPackAuthenticationBufferW(0, buffer, size, user, ref userLength, domain, ref domainLength, password, ref passwordLength))
            {
                log($"入力を読めない: {Marshal.GetLastWin32Error()}");
                return AuthResult.Failed;
            }
            var name = new string(user);
            var domainName = new string(domain);
            // 「PC名\名前」「MicrosoftAccount\メール」の形を分ける
            if (domainName.Length == 0 && CredUIParseUserNameW(name, account, MaxLength, accountDomain, MaxLength) == 0)
            {
                name = new string(account);
                domainName = new string(accountDomain);
            }
            if (!LogonUserW(name, domainName.Length == 0 ? "." : domainName, password, LOGON32_LOGON_INTERACTIVE, LOGON32_PROVIDER_DEFAULT, out var token))
            {
                var error = Marshal.GetLastWin32Error();
                log($"パスワードが通らなかった: {error}");
                // パスワードの無いアカウントは確かめられない
                return error == ERROR_ACCOUNT_RESTRICTION ? AuthResult.Unavailable : AuthResult.Failed;
            }
            try
            {
                // ほかのアカウントのパスワードでは解除しない
                using var identity = new WindowsIdentity(token);
                if (identity.User != currentUser)
                {
                    log("サインインしている人と別のアカウントだった");
                    return AuthResult.Failed;
                }
                return AuthResult.Verified;
            }
            finally
            {
                CloseHandle(token);
            }
        }
        finally
        {
            new Span<char>(password, MaxLength).Clear();
            new Span<char>(user, MaxLength).Clear();
            new Span<char>(domain, MaxLength).Clear();
            new Span<char>(account, MaxLength).Clear();
            new Span<char>(accountDomain, MaxLength).Clear();
        }
    }
}
