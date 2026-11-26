using AppLock.Core;

namespace AppLock.Platform;

/// <summary>
/// 確かめるための組み立て(-p:AppLockTesting=true)だけで使える切り替え。公開する版には入らない。
/// 本物のアプリロックの設定・ログイン時の起動・同時に 1 つの決まりに触れない。
/// - APPLOCK_DATA_DIR: 設定と記録の場所
/// - APPLOCK_FAKE_AUTH: pass / fail / cancel / unavailable(:ミリ秒)。認証を偽物にする
/// - APPLOCK_THEME: Light / Dark
/// </summary>
internal static class Testing
{
#if APPLOCK_TESTING
    public const bool Enabled = true;

    public static string? DataDirectory => Environment.GetEnvironmentVariable("APPLOCK_DATA_DIR") is { Length: > 0 } dir ? dir : null;

    public static string? Theme => Environment.GetEnvironmentVariable("APPLOCK_THEME");

    public static IAuthenticator? FakeAuthenticator(Action<string> log) =>
        Environment.GetEnvironmentVariable("APPLOCK_FAKE_AUTH") is { Length: > 0 } spec ? new Fake(spec, log) : null;

    private sealed class Fake(string spec, Action<string> log) : IAuthenticator
    {
        public async Task<AuthResult> AuthenticateAsync(string message)
        {
            var parts = spec.Split(':');
            var delay = parts.Length > 1 && int.TryParse(parts[1], out var ms) ? ms : 1000;
            log($"偽の認証({spec}): {message}");
            await Task.Delay(delay);
            return parts[0].ToLowerInvariant() switch
            {
                "pass" => AuthResult.Verified,
                "cancel" => AuthResult.Canceled,
                "unavailable" => AuthResult.Unavailable,
                _ => AuthResult.Failed,
            };
        }
    }
#else
    public const bool Enabled = false;

    public static string? DataDirectory => null;

    public static string? Theme => null;

    public static IAuthenticator? FakeAuthenticator(Action<string> log) => null;
#endif
}
