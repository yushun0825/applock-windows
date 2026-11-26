using System.Runtime.InteropServices;

namespace AppLock.Native;

/// <summary>Windows.Security.Credentials.UI.UserConsentVerifierAvailability</summary>
internal enum HelloAvailability
{
    Available = 0,
    DeviceNotPresent = 1,
    NotConfiguredForUser = 2,
    DisabledByPolicy = 3,
    DeviceBusy = 4,
}

/// <summary>Windows.Security.Credentials.UI.UserConsentVerificationResult</summary>
internal enum HelloResult
{
    Verified = 0,
    DeviceNotPresent = 1,
    NotConfiguredForUser = 2,
    DisabledByPolicy = 3,
    DeviceBusy = 4,
    RetriesExhausted = 5,
    Canceled = 6,
}

/// <summary>
/// Windows Hello の本人確認(UserConsentVerifier)を、WinRT の部品を使わずに直接呼ぶ。
/// (Windows SDK の .NET 向けの部品を入れると、アプリが 10 MB ほど大きくなるため)
/// </summary>
internal static class WinRtHello
{
    private const string ClassName = "Windows.Security.Credentials.UI.UserConsentVerifier";
    private static readonly Guid IidStatics = new("AF4F3F91-564C-4DDC-B8B5-973447627C65");
    private static readonly Guid IidInterop = new("39E050C3-4E74-441A-8DC0-B81104DF949C");
    /// <summary>IAsyncOperation&lt;UserConsentVerificationResult&gt;(WinRT の決まりで型の名前から計算した値)</summary>
    private static readonly Guid IidVerificationOperation = new("fd596ffd-2318-558f-9dbe-d21df43764a5");
    private static readonly Guid IidAsyncInfo = new("00000036-0000-0000-C000-000000000046");

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string source, uint length, out IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(IntPtr classId, ref Guid iid, out IntPtr factory);

    public static async Task<HelloAvailability> CheckAvailabilityAsync()
    {
        var operation = StartCheckAvailability();
        try
        {
            return (HelloAvailability)await WaitAsync(operation);
        }
        finally
        {
            Marshal.Release(operation);
        }
    }

    /// <summary>窓を持ち主にして確認の窓を出す(Windows 11 から)</summary>
    public static async Task<HelloResult> RequestVerificationForWindowAsync(IntPtr owner, string message)
    {
        var operation = StartRequestVerification(owner, message);
        try
        {
            return (HelloResult)await WaitAsync(operation);
        }
        finally
        {
            Marshal.Release(operation);
        }
    }

    private static IntPtr Factory(Guid iid)
    {
        Check(WindowsCreateString(ClassName, (uint)ClassName.Length, out var name));
        try
        {
            Check(RoGetActivationFactory(name, ref iid, out var factory));
            return factory;
        }
        finally
        {
            WindowsDeleteString(name);
        }
    }

    private static unsafe IntPtr StartCheckAvailability()
    {
        var statics = Factory(IidStatics);
        try
        {
            // IUserConsentVerifierStatics: [6] CheckAvailabilityAsync
            var vtable = *(IntPtr**)statics;
            var check = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)vtable[6];
            IntPtr operation;
            Check(check(statics, &operation));
            return operation;
        }
        finally
        {
            Marshal.Release(statics);
        }
    }

    private static unsafe IntPtr StartRequestVerification(IntPtr owner, string message)
    {
        var interop = Factory(IidInterop);
        Check(WindowsCreateString(message, (uint)message.Length, out var text));
        try
        {
            // IUserConsentVerifierInterop: [6] RequestVerificationForWindowAsync(HWND, HSTRING, REFIID, void**)
            var vtable = *(IntPtr**)interop;
            var request = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, Guid*, IntPtr*, int>)vtable[6];
            var iid = IidVerificationOperation;
            IntPtr operation;
            Check(request(interop, owner, text, &iid, &operation));
            return operation;
        }
        finally
        {
            WindowsDeleteString(text);
            Marshal.Release(interop);
        }
    }

    /// <summary>IAsyncOperation&lt;列挙型&gt; が終わるのを待って結果を返す(終わったかを 50 ms ごとに見る)</summary>
    private static async Task<int> WaitAsync(IntPtr operation)
    {
        var info = QueryAsyncInfo(operation);
        try
        {
            int status;
            while ((status = Status(info)) == 0)
            {
                await Task.Delay(50);
            }
            return status switch
            {
                1 => Results(operation),
                2 => throw new OperationCanceledException(),
                _ => throw new COMException("Windows Hello の確認が失敗しました", ErrorCode(info)),
            };
        }
        finally
        {
            Marshal.Release(info);
        }
    }

    private static unsafe IntPtr QueryAsyncInfo(IntPtr operation)
    {
        var iid = IidAsyncInfo;
        var vtable = *(IntPtr**)operation;
        var query = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)vtable[0];
        IntPtr info;
        Check(query(operation, &iid, &info));
        return info;
    }

    /// <summary>IAsyncInfo: [7] get_Status(0 = 実行中・1 = 完了・2 = 取り消し・3 = エラー)</summary>
    private static unsafe int Status(IntPtr info)
    {
        var vtable = *(IntPtr**)info;
        var getStatus = (delegate* unmanaged[Stdcall]<IntPtr, int*, int>)vtable[7];
        int status;
        Check(getStatus(info, &status));
        return status;
    }

    /// <summary>IAsyncInfo: [8] get_ErrorCode</summary>
    private static unsafe int ErrorCode(IntPtr info)
    {
        var vtable = *(IntPtr**)info;
        var getError = (delegate* unmanaged[Stdcall]<IntPtr, int*, int>)vtable[8];
        int error;
        return getError(info, &error) == 0 ? error : unchecked((int)0x80004005);
    }

    /// <summary>IAsyncOperation&lt;T&gt;: [8] GetResults</summary>
    private static unsafe int Results(IntPtr operation)
    {
        var vtable = *(IntPtr**)operation;
        var getResults = (delegate* unmanaged[Stdcall]<IntPtr, int*, int>)vtable[8];
        int result;
        Check(getResults(operation, &result));
        return result;
    }

    private static void Check(int hr)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }
}
