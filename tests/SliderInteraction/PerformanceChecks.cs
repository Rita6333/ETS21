using System.Reflection;
using Avalonia.Controls;
using Avalonia.Threading;
using ETS2DayNightModern;

internal static partial class Program
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    private static DispatcherTimer RefreshTimer(Fixture fixture) => (DispatcherTimer)typeof(MainWindow)
        .GetField("_timer", PrivateInstance)!.GetValue(fixture.Window)!;

    private static Task RefreshWindow(Fixture fixture) => (Task)typeof(MainWindow)
        .GetMethod("RefreshAsync", PrivateInstance)!.Invoke(fixture.Window, null)!;

    private static void ScheduleWindowRefresh(Fixture fixture) => typeof(MainWindow)
        .GetMethod("ScheduleRefresh", PrivateInstance)!.Invoke(fixture.Window, null);

    private static void WaitForRefresh(Task refresh)
    {
        WaitFor(() => refresh.IsCompleted, "game refresh did not finish");
        refresh.GetAwaiter().GetResult();
    }

    private static void SlowRefreshCoalesces(Fixture fixture)
    {
        RefreshTimer(fixture).Stop();
        using var release = new ManualResetEventSlim();
        fixture.Service.ProbeHandler = () =>
        {
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("test probe was not released");
            return fixture.Service.State with { GameMinute = 8 * 60 + 15 };
        };
        try
        {
            var before = fixture.Service.ProbeCount;
            var first = RefreshWindow(fixture);
            WaitFor(() => fixture.Service.ActiveProbes == 1, "slow probe did not begin");
            var concurrent = Enumerable.Range(0, 8).Select(_ => RefreshWindow(fixture)).ToArray();
            PumpFor(TimeSpan.FromMilliseconds(30));
            Equal(before + 1, fixture.Service.ProbeCount, "concurrent refreshes started additional game reads");
            Equal(1, fixture.Service.MaximumActiveProbes, "game reads overlapped");
            release.Set();
            WaitForRefresh(Task.WhenAll(concurrent.Append(first)));
            if (fixture.Window.FindControl<TextBlock>("GameTimeText")!.Text != "08:15")
                throw new InvalidOperationException("shared refresh result did not reach the UI");
        }
        finally
        {
            release.Set();
            WaitFor(() => fixture.Service.ActiveProbes == 0, "slow test probe did not stop");
            fixture.Service.ProbeHandler = null;
        }
    }

    private static void MinimizedRefreshPauses(Fixture fixture)
    {
        var timer = RefreshTimer(fixture);
        timer.Interval = TimeSpan.FromMilliseconds(10);
        fixture.Window.WindowState = WindowState.Minimized;
        if (timer.IsEnabled) throw new InvalidOperationException("minimized window retained its refresh timer");
        var before = fixture.Service.ProbeCount;
        PumpFor(TimeSpan.FromMilliseconds(40));
        Equal(before, fixture.Service.ProbeCount, "minimized window continued probing the game");

        fixture.Service.State = fixture.Service.State with { GameMinute = 10 * 60 + 23 };
        fixture.Window.WindowState = WindowState.Normal;
        WaitFor(() => fixture.Service.ProbeCount > before && timer.IsEnabled &&
            fixture.Window.FindControl<TextBlock>("GameTimeText")!.Text == "10:23",
            "restored window did not refresh immediately");
        Near(1, timer.Interval.TotalSeconds, "restored connected window did not resume normal polling");

        fixture.Window.Hide();
        if (timer.IsEnabled) throw new InvalidOperationException("hidden window retained its refresh timer");
        before = fixture.Service.ProbeCount;
        fixture.Service.State = fixture.Service.State with { GameMinute = 11 * 60 + 7 };
        fixture.Window.Show();
        WaitFor(() => fixture.Service.ProbeCount > before && timer.IsEnabled &&
            fixture.Window.FindControl<TextBlock>("GameTimeText")!.Text == "11:07",
            "shown window did not refresh immediately");
    }

    private static void DisconnectedRefreshBacksOff(Fixture fixture)
    {
        var timer = RefreshTimer(fixture);
        timer.Stop();
        fixture.Service.State = GameState.Empty;
        WaitForRefresh(RefreshWindow(fixture));
        ScheduleWindowRefresh(fixture);
        Near(3, timer.Interval.TotalSeconds, "missing game did not use idle polling interval");
        if (!timer.IsEnabled) throw new InvalidOperationException("idle polling stopped game discovery");

        timer.Stop();
        fixture.Service.State = new(true, false, 1, 1, -1, -1);
        WaitForRefresh(RefreshWindow(fixture));
        ScheduleWindowRefresh(fixture);
        Near(3, timer.Interval.TotalSeconds, "unsupported game did not use idle polling interval");

        timer.Stop();
        fixture.Service.State = new(true, true, 1, 1, 12 * 60, -1, 1);
        WaitForRefresh(RefreshWindow(fixture));
        ScheduleWindowRefresh(fixture);
        Near(1, timer.Interval.TotalSeconds, "connected game did not resume normal polling");
    }

    private static void ClosingRefreshDefersDisposal(Fixture fixture)
    {
        var timer = RefreshTimer(fixture);
        timer.Stop();
        var text = fixture.Window.FindControl<TextBlock>("GameTimeText")!;
        var originalText = text.Text;
        using var release = new ManualResetEventSlim();
        fixture.Service.ProbeHandler = () =>
        {
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("closing probe was not released");
            return fixture.Service.State with { GameMinute = 7 * 60 + 42 };
        };
        try
        {
            var refresh = RefreshWindow(fixture);
            WaitFor(() => fixture.Service.ActiveProbes == 1, "closing probe did not begin");
            fixture.Window.Close();
            if (timer.IsEnabled) throw new InvalidOperationException("closed window retained its refresh timer");
            Equal(0, fixture.Service.DisposeCount, "game service was disposed while its probe was running");
            release.Set();
            WaitForRefresh(refresh);
            WaitFor(() => fixture.Service.DisposeCount > 0, "closed window did not release the game service");
            Equal(1, fixture.Service.DisposeCount, "closed window disposed the game service multiple times");
            Equal(0, fixture.Service.ProbesAfterDispose, "a refresh ran after service disposal");
            if (text.Text != originalText) throw new InvalidOperationException("completed probe updated a closed window");
            if (timer.IsEnabled) throw new InvalidOperationException("completed probe restarted a closed window timer");
        }
        finally
        {
            release.Set();
            WaitFor(() => fixture.Service.ActiveProbes == 0, "closing test probe did not stop");
            fixture.Service.ProbeHandler = null;
        }
    }

    private static void PostWriteRefreshIsFresh(Fixture fixture)
    {
        RefreshTimer(fixture).Stop();
        using var release = new ManualResetEventSlim();
        var oldState = fixture.Service.State with { EnvironmentMinute = -1 };
        var reads = 0;
        fixture.Service.ProbeHandler = () =>
        {
            if (Interlocked.Increment(ref reads) == 1)
            {
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("pre-write probe was not released");
                return oldState;
            }
            return oldState with { EnvironmentMinute = 19 * 60 };
        };
        try
        {
            var before = fixture.Service.ProbeCount;
            var staleRead = RefreshWindow(fixture);
            WaitFor(() => fixture.Service.ActiveProbes == 1, "pre-write probe did not begin");
            var apply = (Task)typeof(MainWindow).GetMethod("ApplyAsync", PrivateInstance)!
                .Invoke(fixture.Window, [19 * 60])!;
            WaitFor(() => fixture.Service.AppliedMinutes.Count == 1, "apply did not run during the existing probe");
            release.Set();
            WaitForRefresh(Task.WhenAll(staleRead, apply));
            Equal(before + 2, fixture.Service.ProbeCount, "apply reused a stale game read instead of refreshing afterward");
            Equal(1, fixture.Service.MaximumActiveProbes, "post-write reads overlapped");
            if (fixture.Window.FindControl<TextBlock>("EnvironmentText")!.Text != "环境：已锁定 19:00")
                throw new InvalidOperationException("post-write UI did not show the applied environment");
            Near(19 * 60, fixture.Slider.Value, "stale refresh replaced the applied slider value");
        }
        finally
        {
            release.Set();
            WaitFor(() => fixture.Service.ActiveProbes == 0, "post-write test probe did not stop");
            fixture.Service.ProbeHandler = null;
        }
    }
}
