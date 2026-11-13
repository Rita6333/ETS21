using Avalonia.Controls;
using Avalonia.Threading;
using System.Runtime.InteropServices;

namespace ETS2DayNightModern;

public partial class MainWindow
{
    private void InitializeUpdateRelaunch()
    {
        var id = GetUpdateRelaunchId(_startupArgs);
        Opened += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (_updatesClosing) return;
            if (id is not null)
            {
                WindowState = WindowState.Normal;
                Show();
            }
            Activate();
            // Confirm a usable window after its first layout, not just a live process.
            DispatcherTimer.RunOnce(() =>
            {
                if (id is not null)
                    ConfirmUpdateWindowReady(id);
                else if (!_updatesClosing && IsVisible && WindowState == WindowState.Normal)
                    Activate();
            }, TimeSpan.FromMilliseconds(150));
        }, DispatcherPriority.Loaded);
    }

    internal static string? GetUpdateRelaunchId(string[] arguments)
    {
        for (var i = 0; i + 1 < arguments.Length; i++)
            if (arguments[i] == "--update-relaunch" && Guid.TryParseExact(arguments[i + 1], "N", out var id))
                return id.ToString("N");
        return null;
    }

    private void ConfirmUpdateWindowReady(string id)
    {
        if (_updatesClosing || !IsVisible || !OperatingSystem.IsWindows()) return;
        var platform = TryGetPlatformHandle();
        if (platform?.HandleDescriptor != "HWND" || !IsWindowVisibleAfterUpdate(platform.Handle)) return;
        WindowState = WindowState.Normal;
        Activate();
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ETS2DayNight", "updates");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "relaunch-" + id + ".ready");
            // Accept an identifier from the command line, never a file path.
            File.WriteAllText(path, id);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // The helper retains the backup and reports readiness failure.
        }
    }

    [DllImport("user32.dll", EntryPoint = "IsWindowVisible")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisibleAfterUpdate(nint window);
}
