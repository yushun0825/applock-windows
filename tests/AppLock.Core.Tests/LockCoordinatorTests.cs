using AppLock.Core;

namespace AppLock.Core.Tests;

public sealed class LockCoordinatorTests
{
    private const string Line = "line.exe";
    private const string Notepad = "microsoft.windowsnotepad_8wekyb3d8bbwe!notepad.exe";

    private readonly FakeApps apps = new();
    private readonly FakeAuth auth = new();
    private readonly FakeClock clock = new();
    private LockSettings settings = new()
    {
        Apps =
        [
            new LockedApp { Name = "LINE", ExeName = "LINE.exe" },
            new LockedApp { Name = "メモ帳", ExeName = "Notepad.exe", PackageFamilyName = "Microsoft.WindowsNotepad_8wekyb3d8bbwe" },
        ],
    };
    private readonly LockCoordinator coordinator;

    public LockCoordinatorTests()
    {
        // テストの中では、認証の答えを返した場所で続きが動くようにする
        SynchronizationContext.SetSynchronizationContext(null);
        coordinator = new LockCoordinator(apps, auth, clock, () => settings);
    }

    private async Task AnswerAsync(AuthResult result)
    {
        var task = coordinator.PromptTask!;
        auth.Answer(result);
        await task;
    }

    private void Launch(string id)
    {
        apps.Running.Add(id);
        coordinator.AppLaunched(id);
    }

    [Fact]
    public async Task LaunchFreezesAndAsksThenUnlocksUntilQuit()
    {
        Launch(Line);

        Assert.Equal(Line, coordinator.Authenticating);
        Assert.Contains(Line, apps.Frozen);
        Assert.Contains(Line, apps.Hidden);
        Assert.Equal(["「LINE」のロックを解除"], auth.Messages);

        await AnswerAsync(AuthResult.Verified);

        Assert.Null(coordinator.Authenticating);
        Assert.DoesNotContain(Line, apps.Frozen);
        Assert.DoesNotContain(Line, apps.Hidden);
        Assert.Contains($"reveal {Line}", apps.Calls);
        Assert.Equal(new AppState(AppStateKind.Unlocked, Lifetime.UntilQuit), coordinator.StateOf(Line));

        // 解除中は、新しいプロセスを止めずに通す
        apps.Calls.Clear();
        coordinator.AppLaunched(Line);
        Assert.Equal([$"unfreeze {Line}"], apps.Calls);
        Assert.Single(auth.Messages);
    }

    [Fact]
    public async Task FailureQuitsTheAppWithoutShowingIt()
    {
        Launch(Line);
        await AnswerAsync(AuthResult.Canceled);

        Assert.Contains($"quit {Line}", apps.Calls);
        Assert.DoesNotContain($"reveal {Line}", apps.Calls);
        Assert.DoesNotContain($"unfreeze {Line}", apps.Calls);
        Assert.Equal(AppStateKind.Locked, coordinator.StateOf(Line).Kind);
    }

    [Fact]
    public async Task FailureWithoutQuitKeepsALaunchedAppFrozen()
    {
        settings.QuitAppOnFailure = false;
        Launch(Line);
        await AnswerAsync(AuthResult.Failed);

        Assert.Contains(Line, apps.Frozen);
        Assert.Contains(Line, coordinator.Held);
        Assert.DoesNotContain($"quit {Line}", apps.Calls);

        // 起動し直すと、もう一度認証を求める(認証できなかった直後は待つ)
        coordinator.AppLaunched(Line);
        Assert.Null(coordinator.Authenticating);
        clock.Advance(TimeSpan.FromSeconds(2));
        coordinator.Sweep();
        Assert.Equal(Line, coordinator.Authenticating);
        await AnswerAsync(AuthResult.Verified);
        Assert.DoesNotContain(Line, apps.Frozen);
        Assert.Empty(coordinator.Held);
    }

    [Fact]
    public async Task SecondLaunchWaitsForTheFirstPrompt()
    {
        Launch(Line);
        Launch(Notepad);

        Assert.Equal(Line, coordinator.Authenticating);
        Assert.Equal([Notepad], coordinator.Waiting);
        Assert.Contains(Notepad, apps.Frozen);

        await AnswerAsync(AuthResult.Verified);

        Assert.Equal(Notepad, coordinator.Authenticating);
        Assert.Equal("「メモ帳」のロックを解除", auth.Messages[^1]);
        await AnswerAsync(AuthResult.Verified);
        Assert.True(coordinator.IsUnlocked(Line));
        Assert.True(coordinator.IsUnlocked(Notepad));
    }

    [Fact]
    public async Task AppThatQuitDuringThePromptIsNotUnlocked()
    {
        Launch(Line);
        apps.Running.Remove(Line);
        coordinator.AppTerminated(Line);
        await AnswerAsync(AuthResult.Verified);

        Assert.False(coordinator.IsUnlocked(Line));
        Assert.DoesNotContain($"reveal {Line}", apps.Calls);
    }

    [Fact]
    public async Task QuittingTheAppLocksItAgain()
    {
        Launch(Line);
        await AnswerAsync(AuthResult.Verified);
        apps.Running.Remove(Line);
        coordinator.AppTerminated(Line);

        Assert.False(coordinator.IsUnlocked(Line));
        Launch(Line);
        Assert.Equal(Line, coordinator.Authenticating);
    }

    [Fact]
    public async Task IntervalRuleExpiresAndAsksOnTheNextActivation()
    {
        settings.RelockRule = RelockRule.AfterInterval;
        settings.RelockMinutes = 5;
        Launch(Line);
        await AnswerAsync(AuthResult.Verified);

        Assert.Equal(LifetimeKind.Until, coordinator.StateOf(Line).Lifetime!.Value.Kind);
        clock.Advance(TimeSpan.FromMinutes(4));
        coordinator.AppActivated(Line);
        Assert.Null(coordinator.Authenticating);

        clock.Advance(TimeSpan.FromMinutes(2));
        // 期限が切れても、使っている窓はいきなり隠さない
        apps.Visible.Add(Line);
        apps.Calls.Clear();
        coordinator.Sweep();
        Assert.DoesNotContain($"hide {Line}", apps.Calls);

        coordinator.AppActivated(Line);
        Assert.Equal(Line, coordinator.Authenticating);
        Assert.Contains($"hide {Line}", apps.Calls);
        Assert.Contains($"freeze {Line}", apps.Calls);
    }

    [Fact]
    public async Task SwitchingRuleExpiresWhenAnotherAppComesForward()
    {
        settings.RelockRule = RelockRule.WhenSwitchingAway;
        Launch(Line);
        await AnswerAsync(AuthResult.Verified);

        coordinator.AppActivated(Line);
        Assert.True(coordinator.IsUnlocked(Line));
        coordinator.AppActivated(null);
        Assert.False(coordinator.IsUnlocked(Line));
        coordinator.AppActivated(Line);
        Assert.Equal(Line, coordinator.Authenticating);
    }

    [Fact]
    public async Task SessionLockKeepsUntilQuitUnlocks()
    {
        Launch(Line);
        await AnswerAsync(AuthResult.Verified);
        coordinator.SessionLocked();
        Assert.True(coordinator.IsUnlocked(Line));

        settings.RelockRule = RelockRule.AfterInterval;
        coordinator.SessionLocked();
        Assert.False(coordinator.IsUnlocked(Line));
        Assert.Contains(Line, apps.Hidden);
    }

    [Fact]
    public async Task RelockAllHidesUnlockedApps()
    {
        Launch(Line);
        await AnswerAsync(AuthResult.Verified);
        Assert.True(coordinator.HasUnlockedApps);

        coordinator.RelockAll();
        Assert.False(coordinator.HasUnlockedApps);
        Assert.Contains(Line, apps.Hidden);
    }

    [Fact]
    public void StartHidesRunningAppsWithoutAsking()
    {
        apps.Running.Add(Line);
        apps.Visible.Add(Line);
        coordinator.Start();

        Assert.Contains(Line, apps.Hidden);
        Assert.Null(coordinator.Authenticating);
        Assert.Empty(auth.Messages);
    }

    [Fact]
    public async Task StartAsksForAppsLeftFrozenByAPreviousRun()
    {
        apps.Running.Add(Line);
        apps.FrozenBefore.Add(Line);
        coordinator.Start();

        Assert.Equal(Line, coordinator.Authenticating);
        await AnswerAsync(AuthResult.Verified);
        Assert.DoesNotContain(Line, apps.Frozen);
    }

    [Fact]
    public void ActivationOfAHiddenAppAsks()
    {
        apps.Running.Add(Line);
        coordinator.Start();
        coordinator.AppWindowShown(Line);
        Assert.Null(coordinator.Authenticating);

        coordinator.AppActivated(Line);
        Assert.Equal(Line, coordinator.Authenticating);
    }

    [Fact]
    public async Task ActivationRightAfterAFailureOnlyHides()
    {
        settings.QuitAppOnFailure = false;
        apps.Running.Add(Line);
        coordinator.AppActivated(Line);
        await AnswerAsync(AuthResult.Canceled);

        apps.Calls.Clear();
        coordinator.AppActivated(Line);
        Assert.Null(coordinator.Authenticating);
        Assert.Equal([$"hide {Line}"], apps.Calls);

        clock.Advance(TimeSpan.FromSeconds(2));
        coordinator.AppActivated(Line);
        Assert.Equal(Line, coordinator.Authenticating);
    }

    [Fact]
    public void SweepHidesVisibleWindowsOfAppsNeverUnlocked()
    {
        apps.Running.Add(Line);
        apps.Visible.Add(Line);
        coordinator.Sweep();
        Assert.Contains(Line, apps.Hidden);
    }

    [Fact]
    public void RemovingAHeldAppLetsItRun()
    {
        settings.QuitAppOnFailure = false;
        Launch(Line);
        Launch(Notepad);

        settings = settings.Clone();
        settings.Apps = settings.Apps.Where(a => a.Id != Notepad).ToList();
        coordinator.SettingsChanged();

        Assert.DoesNotContain(Notepad, apps.Frozen);
        Assert.Contains($"show {Notepad}", apps.Calls);
        Assert.Empty(coordinator.Waiting);
        Assert.Equal(Line, coordinator.Authenticating);
    }

    [Fact]
    public async Task RemovingTheAppDuringThePromptLetsItRun()
    {
        Launch(Line);
        settings = settings.Clone();
        settings.Apps = settings.Apps.Where(a => a.Id != Line).ToList();
        coordinator.SettingsChanged();
        await AnswerAsync(AuthResult.Canceled);

        Assert.DoesNotContain($"quit {Line}", apps.Calls);
        Assert.DoesNotContain(Line, apps.Frozen);
        Assert.False(coordinator.IsUnlocked(Line));
    }

    [Fact]
    public async Task UnavailableIsReported()
    {
        var failures = new List<(string, AuthResult)>();
        coordinator.AuthenticationFailed += (id, result) => failures.Add((id, result));
        Launch(Line);
        await AnswerAsync(AuthResult.Unavailable);

        Assert.Equal([(Line, AuthResult.Unavailable)], failures);
        Assert.Contains($"quit {Line}", apps.Calls);
    }

    [Fact]
    public async Task ChangedFiresWhenTheStateChanges()
    {
        var changes = 0;
        coordinator.Changed += () => changes++;
        Launch(Line);
        Assert.True(changes >= 1);
        var before = changes;
        await AnswerAsync(AuthResult.Verified);
        Assert.True(changes > before);
    }

    [Fact]
    public void AppsThatAreNotLockedAreIgnored()
    {
        apps.Running.Add("chrome.exe");
        coordinator.AppLaunched("chrome.exe");
        coordinator.AppActivated("chrome.exe");
        Assert.Null(coordinator.Authenticating);
        Assert.Equal(["unfreeze chrome.exe"], apps.Calls);
    }
}
