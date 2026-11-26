using AppLock.Core;

namespace AppLock.Core.Tests;

/// <summary>アプリの操作の偽物。呼ばれたことを記録し、動いている・窓が出ているを決められる。</summary>
public sealed class FakeApps : IAppControl
{
    public HashSet<string> Running { get; } = [];
    public HashSet<string> Visible { get; } = [];
    public HashSet<string> Frozen { get; } = [];
    public HashSet<string> Hidden { get; } = [];
    public HashSet<string> FrozenBefore { get; } = [];
    public List<string> Calls { get; } = [];

    public IReadOnlyCollection<string> KeptHidden => Hidden;

    public bool IsRunning(string id) => Running.Contains(id);
    public bool IsVisible(string id) => Running.Contains(id) && Visible.Contains(id);
    public bool WasFrozenByPreviousRun(string id) => FrozenBefore.Contains(id);

    public void Hide(string id)
    {
        Calls.Add($"hide {id}");
        Hidden.Add(id);
        Visible.Remove(id);
    }

    public void StopKeepingHidden(string id)
    {
        Calls.Add($"stopHiding {id}");
        Hidden.Remove(id);
    }

    public void Freeze(string id)
    {
        Calls.Add($"freeze {id}");
        Frozen.Add(id);
    }

    public void Unfreeze(string id)
    {
        Calls.Add($"unfreeze {id}");
        Frozen.Remove(id);
    }

    public void Reveal(string id, bool activate)
    {
        Calls.Add(activate ? $"reveal {id}" : $"show {id}");
        Hidden.Remove(id);
        if (Running.Contains(id)) Visible.Add(id);
    }

    public void Quit(string id)
    {
        Calls.Add($"quit {id}");
        Running.Remove(id);
        Visible.Remove(id);
        Frozen.Remove(id);
    }
}

/// <summary>認証の偽物。求められた順に、テストが答えを返す。</summary>
public sealed class FakeAuth : IAuthenticator
{
    private readonly Queue<TaskCompletionSource<AuthResult>> pending = new();
    public List<string> Messages { get; } = [];

    public int PendingCount => pending.Count;

    public Task<AuthResult> AuthenticateAsync(string message)
    {
        Messages.Add(message);
        var source = new TaskCompletionSource<AuthResult>();
        pending.Enqueue(source);
        return source.Task;
    }

    public void Answer(AuthResult result) => pending.Dequeue().SetResult(result);
}

public sealed class FakeClock : IClock
{
    public DateTimeOffset Now { get; set; } = new(2026, 11, 26, 12, 0, 0, TimeSpan.FromHours(9));
    public void Advance(TimeSpan by) => Now += by;
}
