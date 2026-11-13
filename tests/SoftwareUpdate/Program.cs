using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ETS2DayNightModern;

internal static class Program
{
    private const string Source = "https://updates.example.test/update.json";
    private const string Binary = "https://updates.example.test/app.exe";
    private static readonly byte[] Executable = MakeExecutable();
    private static readonly string Hash = Convert.ToHexString(SHA256.HashData(Executable));

    private static async Task<int> Main()
    {
        var tests = new (string Name, Func<Task> Run)[]
        {
            ("settings persist and insecure source is refused", Settings),
            ("new release is detected; current and old releases are ignored", Versions),
            ("manifest fields and size are validated", InvalidManifests),
            ("manifest size is bounded", LargeManifest),
            ("HTTPS redirects work; HTTP downgrade is refused", Redirects),
            ("valid executable downloads with progress", DownloadSuccess),
            ("verified cache is reused; corrupt cache is replaced", DownloadCache),
            ("bad checksum and non-executable downloads are removed", InvalidDownloads),
            ("mismatched and excessive declared lengths are refused", DownloadLengths),
            ("cancelled downloads leave no executable", Cancellation),
            ("changing source invalidates an old release", SourceChange),
            ("missing custom manifest reports a retrieval failure without GitHub fallback", Unpublished),
            ("GitHub latest 404 resolves the official release asset and compares versions", GitHubFallback),
            ("GitHub rechecks use unique lookup URLs while preserving source and asset URLs", GitHubFreshLookup),
            ("GitHub fallback errors and foreign assets never report latest", GitHubFallbackFailures),
            ("installer script parses and safely quotes paths", InstallerScript),
            ("failed or cancelled helper never commits an installation", InstallerHandshakeFailure),
            ("installer replaces, rolls back and rejects corrupt files in an isolated directory", InstallerIntegration),
        };
        var failed = 0;
        foreach (var (name, run) in tests)
        {
            try { await run(); Console.WriteLine("PASS " + name); }
            catch (Exception exception) { failed++; Console.WriteLine("FAIL " + name + ": " + exception); }
        }
        Console.WriteLine($"{tests.Length - failed}/{tests.Length} software update checks passed.");
        return failed == 0 ? 0 : 1;
    }

    private static Task Settings()
    {
        using var fixture = new Fixture(_ => Manifest("2.0.0"));
        fixture.Service.SaveSettings(new UpdateSettings { ManifestUrl = Source, AutoCheck = false, AutoDownload = false });
        using var reloaded = new UpdateService(fixture.Client, fixture.Directory, new Version(1, 0, 0));
        Assert(!reloaded.Settings.AutoCheck && !reloaded.Settings.AutoDownload, "saved preferences not restored");
        Assert(reloaded.Settings.ManifestUrl == Source, "source not restored");
        foreach (var invalid in new[] { "http://updates.example.test/a", "file:///C:/a", "https://user:pass@example.test/a", "https://example.test/a#fragment", "not a URL" })
            Throws<ArgumentException>(() => fixture.Service.SaveSettings(new UpdateSettings { ManifestUrl = invalid }));
        Assert(fixture.Service.Settings.ManifestUrl == Source, "invalid settings replaced the source");
        return Task.CompletedTask;
    }

    private static async Task Versions()
    {
        using var fixture = new Fixture(_ => Manifest("2.0.0"));
        Assert((await fixture.Service.CheckAsync())?.Version == "2.0.0", "new version missed");
        fixture.Handler.Handle = _ => Manifest("1.0.0");
        Assert(await fixture.Service.CheckAsync() is null, "current version offered");
        fixture.Handler.Handle = _ => Manifest("0.9.0");
        Assert(await fixture.Service.CheckAsync() is null, "older version offered");
        fixture.Handler.Handle = _ => Manifest("1.0");
        Assert(await fixture.Service.CheckAsync() is null, "two-component equivalent version offered");
    }

    private static async Task InvalidManifests()
    {
        foreach (var json in new[]
        {
            "not json", "null", "{}",
            JsonSerializer.Serialize(new { version = "v2.0.0", downloadUrl = Binary, sha256 = Hash }),
            JsonSerializer.Serialize(new { version = "2.0.0", downloadUrl = "http://example.test/a", sha256 = Hash }),
            JsonSerializer.Serialize(new { version = "2.0.0", downloadUrl = Binary, sha256 = "bad" }),
            JsonSerializer.Serialize(new { version = "2.0.0", downloadUrl = Binary, sha256 = (string?)null }),
            JsonSerializer.Serialize(new { version = "2.0.0", downloadUrl = Binary, sha256 = Hash, size = 0 }),
            JsonSerializer.Serialize(new { version = "2.0.0", downloadUrl = Binary, sha256 = Hash, size = UpdateService.MaximumDownloadBytes + 1 }),
        })
        {
            using var fixture = new Fixture(_ => TextResponse(json));
            await ThrowsAsync<Exception>(() => fixture.Service.CheckAsync());
        }
    }

    private static async Task LargeManifest()
    {
        using var fixture = new Fixture(_ => TextResponse(new string(' ', 65537)));
        await ThrowsAsync<InvalidDataException>(() => fixture.Service.CheckAsync());
    }

    private static async Task Redirects()
    {
        using var fixture = new Fixture(request => request.RequestUri!.AbsolutePath == "/update.json"
            ? Redirect("https://cdn.example.test/latest.json") : Manifest("2.0.0"));
        Assert(await fixture.Service.CheckAsync() is not null, "HTTPS redirect failed");
        fixture.Handler.Handle = _ => Redirect("http://example.test/update.json");
        await ThrowsAsync<ArgumentException>(() => fixture.Service.CheckAsync());
        fixture.Handler.Handle = _ => Redirect(Source);
        await ThrowsAsync<InvalidDataException>(() => fixture.Service.CheckAsync());
    }

    private static async Task DownloadSuccess()
    {
        using var fixture = new Fixture(request => request.RequestUri!.AbsolutePath == "/update.json"
            ? Manifest("2.0.0") : BytesResponse(Executable));
        var release = (await fixture.Service.CheckAsync())!;
        var progress = new CaptureProgress();
        var download = await fixture.Service.DownloadAsync(release, progress);
        Assert(File.Exists(download.FilePath), "download missing");
        Assert(File.ReadAllBytes(download.FilePath).SequenceEqual(Executable), "download bytes changed");
        Assert(progress.Value?.Percentage == 100, "progress did not finish");
    }

    private static async Task InvalidDownloads()
    {
        using var fixture = new Fixture(_ => BytesResponse(Executable));
        await ThrowsAsync<InvalidDataException>(() => fixture.Service.DownloadAsync(Release() with { Sha256 = new string('0', 64) }));
        AssertNoDownloads(fixture);
        var text = Encoding.UTF8.GetBytes(new string('x', 128));
        fixture.Handler.Handle = _ => BytesResponse(text);
        await ThrowsAsync<InvalidDataException>(() => fixture.Service.DownloadAsync(Release() with
        { Sha256 = Convert.ToHexString(SHA256.HashData(text)), Size = text.Length }));
        AssertNoDownloads(fixture);
    }

    private static async Task DownloadCache()
    {
        using var fixture = new Fixture(_ => BytesResponse(Executable));
        var first = await fixture.Service.DownloadAsync(Release());
        var second = await fixture.Service.DownloadAsync(Release());
        Assert(first.FilePath == second.FilePath && fixture.Handler.Calls == 1, "verified cache was not reused");
        File.WriteAllBytes(first.FilePath, new byte[Executable.Length]);
        var repaired = await fixture.Service.DownloadAsync(Release());
        Assert(repaired.FilePath == first.FilePath && fixture.Handler.Calls == 2, "corrupt cache did not redownload");
        Assert(File.ReadAllBytes(repaired.FilePath).SequenceEqual(Executable), "corrupt cache retained");
        Assert(!Directory.EnumerateFiles(fixture.Directory, "*.part", SearchOption.AllDirectories).Any(), "partial download retained");
    }

    private static async Task DownloadLengths()
    {
        using var fixture = new Fixture(_ => BytesResponse(Executable));
        await ThrowsAsync<InvalidDataException>(() => fixture.Service.DownloadAsync(Release() with { Size = Executable.Length + 1 }));
        fixture.Handler.Handle = _ =>
        {
            var response = BytesResponse(Executable);
            response.Content.Headers.ContentLength = UpdateService.MaximumDownloadBytes + 1;
            return response;
        };
        await ThrowsAsync<InvalidDataException>(() => fixture.Service.DownloadAsync(Release() with { Size = null }));
        AssertNoDownloads(fixture);
    }

    private static async Task Cancellation()
    {
        using var fixture = new Fixture(_ => BytesResponse(Executable));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => fixture.Service.DownloadAsync(Release(), cancellationToken: cancellation.Token));
        AssertNoDownloads(fixture);
    }

    private static async Task SourceChange()
    {
        using var fixture = new Fixture(_ => Manifest("2.0.0"));
        var release = (await fixture.Service.CheckAsync())!;
        fixture.Service.SaveSettings(new UpdateSettings { ManifestUrl = "https://other.example.test/update.json" });
        await ThrowsAsync<InvalidOperationException>(() => fixture.Service.DownloadAsync(release));
        Throws<InvalidOperationException>(() => fixture.Service.LaunchInstaller(new DownloadedUpdate(release, "unused.exe")));
        Assert(fixture.Handler.Calls == 1, "changed source still sent download request");
    }

    private static async Task Unpublished()
    {
        using var fixture = new Fixture(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        try { await fixture.Service.CheckAsync(); throw new Exception("404 was ignored"); }
        catch (InvalidOperationException exception)
        {
            Assert(exception.Message.Contains("无法获取更新信息"), "404 message unclear");
            Assert(!exception.Message.Contains("尚未发布"), "404 falsely claimed no release exists");
        }
        Assert(fixture.Handler.Calls == 1, "custom source unexpectedly queried GitHub");
    }

    private const string GitHubReleaseApi = "https://api.github.com/repos/Rita6333/ETS21/releases/latest";
    private const string GitHubManifest = "https://github.com/Rita6333/ETS21/releases/download/v2.0.0/update.json";

    private static async Task GitHubFallback()
    {
        using var fixture = new Fixture(request =>
        {
            Assert(request.Headers.CacheControl?.NoCache == true, "version lookup allowed a cached response");
            return request.RequestUri!.GetLeftPart(UriPartial.Path) switch
            {
                UpdateService.DefaultManifestUrl => new HttpResponseMessage(HttpStatusCode.NotFound),
                GitHubReleaseApi => GitHubMetadata(),
                GitHubManifest => Manifest("2.0.0"),
                _ => throw new Exception("Unexpected fallback request: " + request.RequestUri)
            };
        });
        fixture.Service.SaveSettings(new UpdateSettings());
        var release = await fixture.Service.CheckAsync();
        Assert(release?.Version == "2.0.0", "fallback missed the published version");
        Assert(release?.SourceUrl == UpdateService.DefaultManifestUrl, "fallback changed the configured source identity");
        Assert(fixture.Handler.Calls == 3, "fallback did not query exactly one API and explicit asset");
        using var current = new UpdateService(fixture.Client, fixture.Directory, new Version(2, 0, 0));
        Assert(await current.CheckAsync() is null, "current version was not recognized through fallback");
        Assert(fixture.Handler.Calls == 6, "current-version fallback made unexpected requests");
    }

    private static async Task GitHubFallbackFailures()
    {
        foreach (var failure in new[] { "api404", "asset404", "foreignAsset", "missingAsset", "metadataTooLarge" })
        {
            using var fixture = new Fixture(request => request.RequestUri!.GetLeftPart(UriPartial.Path) switch
            {
                UpdateService.DefaultManifestUrl => new HttpResponseMessage(HttpStatusCode.NotFound),
                GitHubReleaseApi => failure switch
                {
                    "api404" => new HttpResponseMessage(HttpStatusCode.NotFound),
                    "foreignAsset" => GitHubMetadata("https://foreign.example.test/update.json"),
                    "missingAsset" => TextResponse("{\"tag_name\":\"v2.0.0\",\"draft\":false,\"prerelease\":false,\"assets\":[]}"),
                    "metadataTooLarge" => TextResponse(new string(' ', 256 * 1024 + 1)),
                    _ => GitHubMetadata()
                },
                GitHubManifest => new HttpResponseMessage(HttpStatusCode.NotFound),
                _ => throw new Exception("Unsafe fallback request: " + request.RequestUri)
            });
            fixture.Service.SaveSettings(new UpdateSettings());
            await ThrowsAsync<Exception>(() => fixture.Service.CheckAsync());
            Assert(fixture.Handler.Calls == (failure == "asset404" ? 3 : 2), failure + " retried or fetched an unsafe URL");
        }
    }

    private static HttpResponseMessage GitHubMetadata(string assetUrl = GitHubManifest) => TextResponse(JsonSerializer.Serialize(new
    {
        tag_name = "v2.0.0", draft = false, prerelease = false,
        assets = new[] { new { name = "update.json", browser_download_url = assetUrl } }
    }));

    private static async Task GitHubFreshLookup()
    {
        var latestQueries = new HashSet<string>();
        var metadataQueries = new HashSet<string>();
        using var fixture = new Fixture(request =>
        {
            var url = request.RequestUri!;
            switch (url.GetLeftPart(UriPartial.Path))
            {
                case UpdateService.DefaultManifestUrl:
                    Assert(url.Query.StartsWith("?check=", StringComparison.Ordinal) && latestQueries.Add(url.Query),
                        "repeated latest lookup can reuse stale cache");
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                case GitHubReleaseApi:
                    Assert(url.Query.StartsWith("?check=", StringComparison.Ordinal) && metadataQueries.Add(url.Query),
                        "repeated release API lookup can reuse stale cache");
                    return GitHubMetadata();
                case GitHubManifest:
                    Assert(string.IsNullOrEmpty(url.Query), "explicit release asset URL was rewritten");
                    return Redirect("https://release-assets.githubusercontent.com/update.json?signature=original&expires=123");
                case "https://release-assets.githubusercontent.com/update.json":
                    Assert(url.Query == "?signature=original&expires=123", "signed redirect query was rewritten");
                    return Manifest("2.0.0");
                default:
                    throw new Exception("Unexpected nonce lookup URL: " + url);
            }
        });
        fixture.Service.SaveSettings(new UpdateSettings());
        for (var i = 0; i < 2; i++)
        {
            var release = await fixture.Service.CheckAsync();
            Assert(release?.SourceUrl == UpdateService.DefaultManifestUrl, "nonce changed the configured source identity");
        }
        Assert(latestQueries.Count == 2 && metadataQueries.Count == 2, "lookup nonce was not unique per check");
        fixture.Service.SaveSettings(new UpdateSettings { ManifestUrl = Source + "?channel=stable" });
        fixture.Handler.Handle = request =>
        {
            Assert(request.RequestUri!.AbsoluteUri == Source + "?channel=stable", "custom source query was rewritten");
            return Manifest("1.0.0");
        };
        Assert(await fixture.Service.CheckAsync() is null, "custom source check failed");
    }

    private static async Task InstallerScript()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture(_ => Manifest("2.0.0"));
        var target = Path.Combine(fixture.Directory, "user's 软件 $(literal).exe");
        var script = UpdateInstaller.BuildScript(new DownloadedUpdate(Release(), target + ".download"), target,
            1234, 5678, target + ".stage", target + ".backup", target + ".log");
        Assert(script.Contains("user''s 软件 $(literal).exe'"), "PowerShell paths not single-quoted");
        Assert(script.Contains("$original.StartTime.ToUniversalTime().Ticks -eq $originalStart"), "PID reuse protection missing");
        Assert(script.Contains("[IO.File]::Replace($backup, $target, $stage, $true)"), "rollback missing");
        Assert(script.Contains("[Diagnostics.ProcessWindowStyle]::Normal"), "relaunched application was not explicitly shown normally");
        Assert(script.Contains("'--update-relaunch ' + $startupToken"), "window readiness handshake argument missing");
        Assert(script.Contains("Application confirmed that its window opened."), "installer did not await window readiness");
        var scriptPath = Path.Combine(fixture.Directory, "installer.ps1");
        File.WriteAllText(scriptPath, script, new UTF8Encoding(true));
        var parser = "$tokens=$null;$errors=$null;[System.Management.Automation.Language.Parser]::ParseFile('"
            + scriptPath.Replace("'", "''") + "',[ref]$tokens,[ref]$errors)|Out-Null;if($errors.Count){$errors|Out-String|Write-Output;exit 1}";
        var info = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-NonInteractive");
        info.ArgumentList.Add("-EncodedCommand");
        info.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(parser)));
        using var process = Process.Start(info)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert(process.ExitCode == 0, "installer syntax error: " + output + error);
        Assert(!File.Exists(target), "script parser executed the installer");
    }

    private static async Task InstallerIntegration()
    {
        if (!OperatingSystem.IsWindows()) return;
        var dummyRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../_case/update_dummy"));
        var oldSource = Path.Combine(dummyRoot, "old/Dummy.exe");
        var newSource = Path.Combine(dummyRoot, "new/Dummy.exe");
        var failedSource = Path.Combine(dummyRoot, "failed/Dummy.exe");
        var noWindowSource = Path.Combine(dummyRoot, "no-window/Dummy.exe");
        if (!File.Exists(oldSource) || !File.Exists(newSource) || !File.Exists(failedSource) || !File.Exists(noWindowSource))
            throw new Exception("Build tests/SoftwareUpdateDummy into _case/update_dummy/{old,new,failed,no-window} first.");
        using var fixture = new Fixture(_ => BytesResponse(Executable));
        var previousRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        var runtimeRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../_deps/dotnet8/sdk"));
        if (!Directory.Exists(runtimeRoot))
            runtimeRoot = Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "../../.."));
        try
        {
            Environment.SetEnvironmentVariable("DOTNET_ROOT", runtimeRoot);
            await RunInstallCase("success", newSource, expected: "new", corruptHash: false);
            await RunInstallCase("rollback", failedSource, expected: "old", corruptHash: false);
            await RunInstallCase("bad-hash", newSource, expected: "old", corruptHash: true);
            await RunInstallCase("no-window", noWindowSource, expected: "old", corruptHash: false);
        }
        finally { Environment.SetEnvironmentVariable("DOTNET_ROOT", previousRoot); }

        async Task RunInstallCase(string name, string newFile, string expected, bool corruptHash)
        {
            var directory = Path.Combine(fixture.Directory, name + " user's $literal");
            Directory.CreateDirectory(directory);
            var target = Path.Combine(directory, "Dummy.exe");
            File.Copy(oldSource, target);
            var download = Path.Combine(directory, "download.exe");
            File.Copy(newFile, download);
            var actualHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(download)));
            var release = Release() with { Sha256 = corruptHash ? new string('0', 64) : actualHash };
            var stage = Path.Combine(directory, "stage.exe");
            var backup = Path.Combine(directory, "backup.exe");
            var log = Path.Combine(directory, "install.log");
            var ready = Path.Combine(directory, "ready");
            var commit = Path.Combine(directory, "commit");
            var script = UpdateInstaller.BuildScript(new DownloadedUpdate(release, download), target,
                int.MaxValue, 1, stage, backup, log, ready, commit, startupTimeoutSeconds: 3);
            var info = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            info.ArgumentList.Add("-NoProfile"); info.ArgumentList.Add("-NonInteractive"); info.ArgumentList.Add("-EncodedCommand");
            info.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
            using var helper = Process.Start(info)!;
            await UpdateInstaller.ConfirmReadyAsync(helper, ready, commit, CancellationToken.None);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await helper.WaitForExitAsync(timeout.Token);
            var stderr = await helper.StandardError.ReadToEndAsync();
            Assert(helper.ExitCode == 0, name + " helper error: " + stderr);
            var messages = File.ReadAllText(Path.Combine(directory, "started.log"));
            Assert(messages.TrimEnd().EndsWith(expected), name + " did not start expected application: " + messages + "\n" + File.ReadAllText(log));
            var expectedHash = SHA256.HashData(File.ReadAllBytes(expected == "new" ? newSource : oldSource));
            Assert(SHA256.HashData(File.ReadAllBytes(target)).SequenceEqual(expectedHash), name + " left wrong executable");
            Assert(!File.Exists(stage), name + " left staged executable");
            Assert(File.Exists(download), name + " deleted shared download cache");
            // The acknowledged dummy remains alive briefly so readiness is tested
            // against a running application; let that owned process finish before cleanup.
            await Task.Delay(1250);
            if (name == "success") Assert(File.Exists(backup), "successful update did not keep backup");
            if (name == "rollback") Assert(File.ReadAllText(log).Contains("Previous version restored"), "rollback not recorded");
            if (name == "no-window")
            {
                Assert(File.ReadAllText(log).Contains("Stopped the unconfirmed application"), "invisible child was left running");
                Assert(File.ReadAllText(log).Contains("Previous version restored"), "invisible child did not trigger rollback");
                Assert(!File.ReadAllText(log).Contains("Installed successfully"), "live process without a ready window was reported as success");
            }
            if (corruptHash) Assert(!File.Exists(backup), "bad hash replaced original");
        }
    }

    private static async Task InstallerHandshakeFailure()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture(_ => Manifest("2.0.0"));
        var ready = Path.Combine(fixture.Directory, "ready");
        var commit = Path.Combine(fixture.Directory, "commit");
        using (var helper = Start("exit 0"))
            await ThrowsAsync<IOException>(() => UpdateInstaller.ConfirmReadyAsync(helper, ready, commit, CancellationToken.None));
        Assert(!File.Exists(commit), "failed helper was committed");
        using (var helper = Start("[Threading.Thread]::Sleep(30000)"))
        using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
        {
            await ThrowsAsync<OperationCanceledException>(() => UpdateInstaller.ConfirmReadyAsync(helper, ready, commit, cancellation.Token));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await helper.WaitForExitAsync(timeout.Token);
            Assert(!File.Exists(commit), "cancelled helper was committed");
        }

        static Process Start(string script)
        {
            var info = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
                UseShellExecute = false, CreateNoWindow = true,
            };
            info.ArgumentList.Add("-NoProfile"); info.ArgumentList.Add("-NonInteractive"); info.ArgumentList.Add("-EncodedCommand");
            info.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
            return Process.Start(info)!;
        }
    }

    private static UpdateRelease Release() => new("2.0.0", Binary, Hash, Executable.Length, "Changes", Source);
    private static HttpResponseMessage Manifest(string version) => TextResponse(JsonSerializer.Serialize(new
    { version, downloadUrl = Binary, sha256 = Hash, size = Executable.Length, notes = "Changes" }));
    private static HttpResponseMessage TextResponse(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };
    private static HttpResponseMessage BytesResponse(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    private static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Redirect);
        response.Headers.Location = new Uri(location);
        return response;
    }
    private static byte[] MakeExecutable()
    {
        var bytes = new byte[128];
        bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(60), 64);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(64), 0x4550);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(68), 0x8664);
        return bytes;
    }
    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    private static void AssertNoDownloads(Fixture fixture) => Assert(!Directory.EnumerateFiles(fixture.Directory, "*.exe", SearchOption.AllDirectories).Any(), "failed download was retained");
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }

    private sealed class CaptureProgress : IProgress<UpdateProgress>
    { public UpdateProgress? Value { get; private set; } public void Report(UpdateProgress value) => Value = value; }
    private sealed class FakeHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Handle { get; set; }
        public int Calls { get; private set; }
        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> handle) => Handle = handle;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); Calls++; return Task.FromResult(Handle(request)); }
    }
    private sealed class Fixture : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "ets2-update-tests-" + Guid.NewGuid().ToString("N"));
        public FakeHandler Handler { get; }
        public HttpClient Client { get; }
        public UpdateService Service { get; }
        public Fixture(Func<HttpRequestMessage, HttpResponseMessage> handle)
        {
            Handler = new FakeHandler(handle);
            Client = new HttpClient(Handler);
            Service = new UpdateService(Client, Directory, new Version(1, 0, 0));
            Service.SaveSettings(new UpdateSettings { ManifestUrl = Source });
        }
        public void Dispose()
        {
            Service.Dispose(); Client.Dispose();
            if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true);
        }
    }
}
