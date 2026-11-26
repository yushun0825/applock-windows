using AppLock.Core;

namespace AppLock.Core.Tests;

public sealed class SettingsTests
{
    [Fact]
    public void DefaultsMatchTheMacVersion()
    {
        var settings = new LockSettings();
        Assert.Empty(settings.Apps);
        Assert.Equal(RelockRule.WhenAppQuits, settings.RelockRule);
        Assert.Equal(5, settings.RelockMinutes);
        Assert.True(settings.QuitAppOnFailure);
        Assert.True(settings.ProtectSelf);
    }

    [Fact]
    public void NormalizeFixesImpossibleValues()
    {
        var settings = new LockSettings
        {
            RelockMinutes = 999,
            RelockRule = (RelockRule)42,
            Apps =
            [
                new LockedApp { Name = "LINE", ExeName = "LINE.exe" },
                new LockedApp { Name = "LINE 2", ExeName = "line.EXE" },
                new LockedApp { Name = "エクスプローラー", ExeName = "explorer.exe" },
                new LockedApp { Name = "", ExeName = "Slack.exe" },
                new LockedApp { Name = "空", ExeName = "" },
            ],
        };
        settings.Normalize();

        Assert.Equal(LockSettings.MaxMinutes, settings.RelockMinutes);
        Assert.Equal(RelockRule.WhenAppQuits, settings.RelockRule);
        Assert.Equal(["LINE", "Slack"], settings.Apps.Select(a => a.Name));
    }

    [Fact]
    public void RoundTripsThroughJson()
    {
        var settings = new LockSettings
        {
            Apps = [new LockedApp { Name = "メモ帳", ExeName = "Notepad.exe", PackageFamilyName = "Microsoft.WindowsNotepad_8wekyb3d8bbwe", AppUserModelId = "Microsoft.WindowsNotepad_8wekyb3d8bbwe!App" }],
            RelockRule = RelockRule.AfterInterval,
            RelockMinutes = 10,
            QuitAppOnFailure = false,
            ProtectSelf = false,
        };
        var json = SettingsJson.Serialize(settings);
        Assert.Contains("\"afterInterval\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"id\"", json);

        var read = SettingsJson.Parse(json);
        Assert.Equal(settings.Apps, read.Apps);
        Assert.Equal(RelockRule.AfterInterval, read.RelockRule);
        Assert.Equal(10, read.RelockMinutes);
        Assert.False(read.QuitAppOnFailure);
        Assert.False(read.ProtectSelf);
    }

    [Fact]
    public void MissingOrUnknownValuesUseTheDefaults()
    {
        var read = SettingsJson.Parse("""
            {
              // 前の版・知らない値
              "apps": [ { "exeName": "LINE.exe" } ],
              "relockRule": "everyTuesday",
              "somethingNew": true,
            }
            """);
        Assert.Equal("LINE", Assert.Single(read.Apps).Name);
        Assert.Equal(RelockRule.WhenAppQuits, read.RelockRule);
        Assert.True(read.QuitAppOnFailure);
        Assert.True(read.ProtectSelf);
    }

    [Fact]
    public void CloneDoesNotShareTheList()
    {
        var settings = new LockSettings { Apps = [new LockedApp { Name = "LINE", ExeName = "LINE.exe" }] };
        var copy = settings.Clone();
        copy.Apps.Add(new LockedApp { Name = "Slack", ExeName = "slack.exe" });
        Assert.Single(settings.Apps);
        Assert.Equal(2, copy.LockedIds.Count);
    }
}

public sealed class MatcherTests
{
    private readonly AppMatcher matcher = new(
    [
        new LockedApp { Name = "LINE", ExeName = "LINE.exe" },
        new LockedApp { Name = "メモ帳", ExeName = "Notepad.exe", PackageFamilyName = "Microsoft.WindowsNotepad_8wekyb3d8bbwe" },
    ]);

    [Fact]
    public void MatchesByFileNameIgnoringCase()
    {
        Assert.True(matcher.IsCandidate("line.exe"));
        Assert.Equal("line.exe", matcher.Match("Line.EXE", null));
        Assert.Equal("line.exe", matcher.Match("LINE.exe", "Some.Package_123"));
    }

    [Fact]
    public void PackagedAppsNeedTheirPackage()
    {
        Assert.True(matcher.NeedsPackage("notepad.exe"));
        Assert.False(matcher.NeedsPackage("LINE.exe"));
        Assert.Equal("microsoft.windowsnotepad_8wekyb3d8bbwe!notepad.exe", matcher.Match("notepad.exe", "Microsoft.WindowsNotepad_8wekyb3d8bbwe"));
        // System32 の notepad.exe(パッケージではない)は別のアプリ
        Assert.Null(matcher.Match("notepad.exe", null));
        Assert.Null(matcher.Match("chrome.exe", null));
    }

    [Fact]
    public void SystemPartsCannotBeLocked()
    {
        Assert.False(SystemApps.CanLock("explorer.exe"));
        Assert.False(SystemApps.CanLock("AppLock.exe"));
        Assert.True(SystemApps.CanLock("LINE.exe"));
        Assert.True(SystemApps.IsIgnoredForSwitching("explorer.exe", "Shell_TrayWnd"));
        Assert.False(SystemApps.IsIgnoredForSwitching("explorer.exe", "CabinetWClass"));
        Assert.True(SystemApps.IsIgnoredForSwitching("CredentialUIBroker.exe", null));
        Assert.True(SystemApps.LooksLikeUninstaller("Uninstall Foo"));
        Assert.True(SystemApps.LooksLikeUninstaller("Fooのアンインストール"));
    }
}

public sealed class LedgerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void IntervalUnlockEndsButIsRemembered()
    {
        var ledger = new UnlockLedger();
        ledger.Unlock("a", Lifetime.UntilTime(Now.AddMinutes(5)));
        Assert.True(ledger.IsUnlocked("a", Now.AddMinutes(4)));
        Assert.False(ledger.IsUnlocked("a", Now.AddMinutes(5)));
        Assert.True(ledger.WasRevealed("a"));
    }

    [Fact]
    public void SwitchingExpiresOnlyOtherSwitchAwayUnlocks()
    {
        var ledger = new UnlockLedger();
        ledger.Unlock("a", Lifetime.UntilSwitchAway);
        ledger.Unlock("b", Lifetime.UntilSwitchAway);
        ledger.Unlock("c", Lifetime.UntilQuit);
        ledger.SwitchedTo("a");
        Assert.True(ledger.IsUnlocked("a", Now));
        Assert.False(ledger.IsUnlocked("b", Now));
        Assert.True(ledger.IsUnlocked("c", Now));
    }

    [Fact]
    public void ForgetAllCanKeepUntilQuit()
    {
        var ledger = new UnlockLedger();
        ledger.Unlock("a", Lifetime.UntilQuit);
        ledger.Unlock("b", Lifetime.UntilSwitchAway);
        Assert.Equal(["b"], ledger.ForgetAll(keepingUntilQuit: true));
        Assert.Equal(["a"], ledger.ForgetAll(keepingUntilQuit: false));
        Assert.True(ledger.IsEmpty);
    }
}
