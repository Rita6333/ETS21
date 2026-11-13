using System.Collections.Concurrent;

namespace ETS2DayNightModern;

// The regression runner compiles only the window and these substitutes. It never
// loads the production game service, requests elevation, or touches a process.
public readonly record struct GameState(
    bool Alive, bool Patchable, uint Pid, nint ExeBase,
    int GameMinute, int EnvironmentMinute,
    nuint EnvironmentRefreshRva = 0, bool AccessDenied = false)
{
    public static GameState Empty => new(false, false, 0, 0, -1, -1);
}

public readonly record struct OperationResult(bool Success, string Message);

public sealed class GameTimeService : IDisposable
{
    public static GameTimeService Latest { get; private set; } = null!;
    public static GameState InitialState { get; set; } = new(true, true, 1, 1, -1, -1, 1);
    public ConcurrentQueue<int> AppliedMinutes { get; } = new();
    private readonly object _stateLock = new();
    private GameState _state = InitialState;
    private Func<GameState>? _probeHandler;
    private int _probeCount;
    private int _activeProbes;
    private int _maximumActiveProbes;
    private int _disposeCount;
    private int _probesAfterDispose;

    public GameState State
    {
        get { lock (_stateLock) return _state; }
        set { lock (_stateLock) _state = value; }
    }
    public Func<GameState>? ProbeHandler
    {
        get => Volatile.Read(ref _probeHandler);
        set => Volatile.Write(ref _probeHandler, value);
    }
    public int ProbeCount => Volatile.Read(ref _probeCount);
    public int ActiveProbes => Volatile.Read(ref _activeProbes);
    public int MaximumActiveProbes => Volatile.Read(ref _maximumActiveProbes);
    public int DisposeCount => Volatile.Read(ref _disposeCount);
    public int ProbesAfterDispose => Volatile.Read(ref _probesAfterDispose);

    public GameTimeService() => Latest = this;
    public GameState Probe()
    {
        if (DisposeCount != 0)
        {
            Interlocked.Increment(ref _probesAfterDispose);
            throw new ObjectDisposedException(nameof(GameTimeService));
        }
        Interlocked.Increment(ref _probeCount);
        var active = Interlocked.Increment(ref _activeProbes);
        int maximum;
        do
        {
            maximum = MaximumActiveProbes;
            if (active <= maximum) break;
        } while (Interlocked.CompareExchange(ref _maximumActiveProbes, active, maximum) != maximum);
        try { return ProbeHandler?.Invoke() ?? State; }
        finally { Interlocked.Decrement(ref _activeProbes); }
    }
    public OperationResult Apply(GameState state, int minute)
    {
        AppliedMinutes.Enqueue(minute);
        return new(true, "Test apply");
    }
    public OperationResult Restore(GameState state) => new(true, "Test restore");
    public void Dispose() => Interlocked.Increment(ref _disposeCount);
}

internal static class Elevation
{
    public static bool IsAdministrator() => true;
    public static bool TryRelaunch(string arguments) =>
        throw new InvalidOperationException("Elevation must never run in this test.");
}
