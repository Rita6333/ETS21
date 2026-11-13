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

public sealed class GameTimeService
{
    public static GameTimeService Latest { get; private set; } = null!;
    public static GameState InitialState { get; set; } = new(true, true, 1, 1, -1, -1, 1);
    public ConcurrentQueue<int> AppliedMinutes { get; } = new();
    private readonly GameState _state = InitialState;

    public GameTimeService() => Latest = this;
    public GameState Probe() => _state;
    public OperationResult Apply(GameState state, int minute)
    {
        AppliedMinutes.Enqueue(minute);
        return new(true, "Test apply");
    }
    public OperationResult Restore(GameState state) => new(true, "Test restore");
}

internal static class Elevation
{
    public static bool IsAdministrator() => true;
    public static bool TryRelaunch(string arguments) =>
        throw new InvalidOperationException("Elevation must never run in this test.");
}
