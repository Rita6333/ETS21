using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace ETS2DayNightModern;

internal static class UpdateInstaller
{
    internal static async Task LaunchAsync(DownloadedUpdate update, string dataDirectory, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("自动安装仅支持 Windows。");
        var target = Environment.ProcessPath;
        if (string.IsNullOrEmpty(target) || !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileNameWithoutExtension(target).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("请从发布后的 EXE 启动软件再安装更新。");
        if (!File.Exists(update.FilePath)) throw new FileNotFoundException("更新文件已被移除，请重新下载。");
        var executableDirectory = Path.GetDirectoryName(target)!;
        var id = Guid.NewGuid().ToString("N");
        var stage = Path.Combine(executableDirectory, ".ets2-update-" + id + ".exe");
        var backup = target + ".backup-" + id;
        // Discover a protected destination before asking the application to quit.
        try
        {
            using (new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            File.Delete(stage);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { throw new IOException("无法写入软件所在文件夹，请移到可写文件夹或以管理员身份重试。", exception); }
        Directory.CreateDirectory(dataDirectory);
        var log = Path.Combine(dataDirectory, "install-" + id + ".log");
        var ready = Path.Combine(dataDirectory, "ready-" + id);
        var commit = Path.Combine(dataDirectory, "commit-" + id);
        using var current = Process.GetCurrentProcess();
        var script = BuildScript(update, target, current.Id,
            current.StartTime.ToUniversalTime().Ticks, stage, backup, log, ready, commit, id);
        var info = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = dataDirectory,
        };
        info.ArgumentList.Add("-NoLogo");
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-NonInteractive");
        info.ArgumentList.Add("-ExecutionPolicy");
        info.ArgumentList.Add("Bypass");
        info.ArgumentList.Add("-EncodedCommand");
        info.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        using var helper = Process.Start(info) ?? throw new IOException("无法启动更新安装程序。");
        AllowSetForegroundWindow((uint)helper.Id);
        await ConfirmReadyAsync(helper, ready, commit, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task ConfirmReadyAsync(Process helper, string ready, string commit, CancellationToken cancellationToken)
    {
        try
        {
            var deadline = Stopwatch.StartNew();
            while (!File.Exists(ready))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (helper.HasExited) throw new IOException("更新安装程序未能启动，当前软件将保持运行。");
                if (deadline.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("更新安装程序启动超时，当前软件将保持运行。");
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.WriteAllText(commit, "commit");
        }
        catch
        {
            try { if (!helper.HasExited) helper.Kill(entireProcessTree: true); } catch { }
            UpdateService.TryDelete(ready);
            UpdateService.TryDelete(commit);
            throw;
        }
    }

    internal static string BuildScript(DownloadedUpdate update, string target, int processId,
        long startTicks, string stage, string backup, string log, string? ready = null, string? commit = null,
        string? startupId = null, int startupTimeoutSeconds = 20) => $$"""
        $ErrorActionPreference = 'Stop'
        $download = {{Quote(update.FilePath)}}
        $target = {{Quote(target)}}
        $stage = {{Quote(stage)}}
        $backup = {{Quote(backup)}}
        $log = {{Quote(log)}}
        $ready = {{Quote(ready ?? "")}}
        $commit = {{Quote(commit ?? "")}}
        $startupToken = {{Quote(startupId ?? Guid.NewGuid().ToString("N"))}}
        $startupReady = [IO.Path]::Combine([IO.Path]::GetDirectoryName($log), ('relaunch-' + $startupToken + '.ready'))
        $startupTimeout = {{Math.Clamp(startupTimeoutSeconds, 2, 60).ToString(CultureInfo.InvariantCulture)}}
        $expectedHash = {{Quote(update.Release.Sha256)}}
        $originalId = {{processId.ToString(CultureInfo.InvariantCulture)}}
        $originalStart = [long]{{Quote(startTicks.ToString(CultureInfo.InvariantCulture))}}
        $replaced = $false
        function Write-InstallLog([string]$message) {
            try { [IO.File]::AppendAllText($log, ([DateTime]::UtcNow.ToString('o') + ' ' + $message + [Environment]::NewLine)) } catch { }
        }
        function Original-IsRunning {
            try {
                $original = [Diagnostics.Process]::GetProcessById($originalId)
                try { return $original.StartTime.ToUniversalTime().Ticks -eq $originalStart } finally { $original.Dispose() }
            } catch { return $false }
        }
        function Start-Application([bool]$requireReady) {
            if ([IO.File]::Exists($startupReady)) { [IO.File]::Delete($startupReady) }
            $start = [Diagnostics.ProcessStartInfo]::new()
            $start.FileName = $target
            $start.WorkingDirectory = [IO.Path]::GetDirectoryName($target)
            $start.UseShellExecute = $true
            $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Normal
            $start.Arguments = '--update-relaunch ' + $startupToken
            $app = [Diagnostics.Process]::Start($start)
            if ($null -eq $app) { throw 'Unable to start application.' }
            try {
                $foregroundPermission = [UpdateWindowNative]::AllowSetForegroundWindow([uint32]$app.Id)
                Write-InstallLog ('Foreground permission forwarded to application: ' + $foregroundPermission)
                Write-InstallLog ('Started application PID ' + $app.Id + '; waiting for its window.')
                $deadline = [DateTime]::UtcNow.AddSeconds($startupTimeout)
                $restored = $false
                while ($true) {
                    $app.Refresh()
                    if ($app.HasExited) { throw ('Application exited before opening its window; code ' + $app.ExitCode) }
                    $window = $app.MainWindowHandle
                    if ($window -ne [IntPtr]::Zero -and -not $restored) {
                        [void][UpdateWindowNative]::ShowWindowAsync($window, 9)
                        $foreground = [UpdateWindowNative]::SetForegroundWindow($window)
                        Write-InstallLog ('Window restore requested; foreground request accepted: ' + $foreground)
                        $restored = $true
                    }
                    if ([IO.File]::Exists($startupReady) -and [IO.File]::ReadAllText($startupReady) -eq $startupToken) {
                        Write-InstallLog 'Application confirmed that its window opened.'
                        return
                    }
                    if (-not $requireReady -and $window -ne [IntPtr]::Zero -and [UpdateWindowNative]::IsWindowVisible($window)) {
                        Write-InstallLog 'Previous application window is visible.'
                        return
                    }
                    if ([DateTime]::UtcNow -ge $deadline) { throw 'Application did not confirm its window opened before the timeout.' }
                    [Threading.Thread]::Sleep(100)
                }
            } catch {
                try {
                    if (-not $app.HasExited) {
                        $app.Kill()
                        [void]$app.WaitForExit(5000)
                        Write-InstallLog 'Stopped the unconfirmed application before rollback.'
                    }
                } catch { Write-InstallLog ('Unable to stop unconfirmed application: ' + $_.Exception.ToString()) }
                throw
            } finally { $app.Dispose() }
        }
        try {
            Import-Module ([IO.Path]::Combine($PSHOME, 'Modules', 'Microsoft.PowerShell.Utility', 'Microsoft.PowerShell.Utility.psd1'))
            Add-Type -TypeDefinition @'
        using System;
        using System.Runtime.InteropServices;
        public static class UpdateWindowNative {
            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool AllowSetForegroundWindow(uint processId);
            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool IsWindowVisible(IntPtr window);
            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool ShowWindowAsync(IntPtr window, int command);
            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool SetForegroundWindow(IntPtr window);
        }
        '@

            if ($ready -and $commit) {
                [IO.File]::WriteAllText($ready, 'ready')
                $deadline = [DateTime]::UtcNow.AddSeconds(10)
                while (-not [IO.File]::Exists($commit)) {
                    if ([DateTime]::UtcNow -ge $deadline) { throw 'Update was not confirmed.' }
                    [Threading.Thread]::Sleep(50)
                }
            }
            Write-InstallLog 'Waiting for application to close.'
            $deadline = [DateTime]::UtcNow.AddSeconds(60)
            while (Original-IsRunning) {
                if ([DateTime]::UtcNow -ge $deadline) { throw 'Application did not close within 60 seconds.' }
                [Threading.Thread]::Sleep(200)
            }
            if ([IO.File]::Exists($stage) -or [IO.File]::Exists($backup)) { throw 'Unique update path already exists.' }
            [IO.File]::Copy($download, $stage, $false)
            $hashAlgorithm = [Security.Cryptography.SHA256]::Create()
            $hashStream = [IO.File]::OpenRead($stage)
            try { $actualHash = [BitConverter]::ToString($hashAlgorithm.ComputeHash($hashStream)).Replace('-', '') }
            finally { $hashStream.Dispose(); $hashAlgorithm.Dispose() }
            if ($actualHash -ne $expectedHash) { throw 'SHA256 verification failed.' }
            $deadline = [DateTime]::UtcNow.AddSeconds(10)
            while ($true) {
                try {
                    [IO.File]::Replace($stage, $target, $backup, $true)
                    $replaced = $true
                    break
                } catch {
                    if ([DateTime]::UtcNow -ge $deadline) { throw }
                    [Threading.Thread]::Sleep(250)
                }
            }
            Start-Application $true
            Write-InstallLog ('Installed successfully. Previous version: ' + $backup)
        } catch {
            Write-InstallLog ('Install failed: ' + $_.Exception.ToString())
            if ($replaced -and [IO.File]::Exists($backup)) {
                try {
                    [IO.File]::Replace($backup, $target, $stage, $true)
                    Write-InstallLog 'Previous version restored.'
                } catch { Write-InstallLog ('Rollback failed: ' + $_.Exception.ToString()) }
            }
            if (-not (Original-IsRunning)) {
                try { Start-Application $false } catch { Write-InstallLog ('Restart failed: ' + $_.Exception.ToString()) }
            }
        } finally {
            try { [IO.File]::Delete($stage) } catch { }
            if ($ready) { try { [IO.File]::Delete($ready) } catch { } }
            if ($commit) { try { [IO.File]::Delete($commit) } catch { } }
            try { [IO.File]::Delete($startupReady) } catch { }
        }
        """;

    private static string Quote(string value) => "'" + value.Replace("'", "''") + "'";

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
