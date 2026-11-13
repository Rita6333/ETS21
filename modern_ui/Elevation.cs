using System.Diagnostics;
using System.ComponentModel;
using System.Security.Principal;
using System.Runtime.Versioning;

namespace ETS2DayNightModern;

internal static class Elevation
{
    [SupportedOSPlatform("windows")]
    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static bool TryRelaunch(string arguments)
    {
        try
        {
            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable)) return false;
            Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory,
            });
            return true;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }
}
