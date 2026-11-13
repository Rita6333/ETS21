using System.Buffers.Binary;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace ETS2DayNightModern;

public sealed record UpdateSettings
{
    public string ManifestUrl { get; init; } = UpdateService.DefaultManifestUrl;
    public bool AutoCheck { get; init; } = true;
    public bool AutoDownload { get; init; } = true;
}

public sealed record UpdateRelease(string Version, string DownloadUrl, string Sha256,
    long? Size, string Notes, string SourceUrl);

public sealed record DownloadedUpdate(UpdateRelease Release, string FilePath);

public sealed record UpdateProgress(long BytesReceived, long? TotalBytes)
{
    public double? Percentage => TotalBytes > 0 ? BytesReceived * 100d / TotalBytes.Value : null;
}

/// <summary>Portable application updater. Checks and downloads never modify the running executable.</summary>
public sealed class UpdateService : IDisposable
{
    public const string DefaultManifestUrl = "https://github.com/Rita6333/ETS21/releases/latest/download/update.json";
    public const long MaximumDownloadBytes = 200L * 1024 * 1024;
    private const int MaximumManifestBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly Version _currentVersion;
    private readonly string _dataDirectory;
    private readonly string _settingsPath;

    public UpdateSettings Settings { get; private set; }
    public string CurrentVersion => _currentVersion.Revision > 0
        ? _currentVersion.ToString(4) : _currentVersion.ToString(3);
    public bool HasUpdateSource => !string.IsNullOrWhiteSpace(Settings.ManifestUrl);

    public UpdateService(HttpClient? client = null, string? dataDirectory = null, Version? currentVersion = null)
    {
        _ownsClient = client is null;
        _client = client ?? new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        }) { Timeout = Timeout.InfiniteTimeSpan };
        _currentVersion = Normalize(currentVersion ?? typeof(UpdateService).Assembly.GetName().Version ?? new Version(1, 0, 0));
        _dataDirectory = dataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ETS2DayNight", "updates");
        _settingsPath = Path.Combine(_dataDirectory, "settings.json");
        Settings = LoadSettings();
    }

    public void SaveSettings(UpdateSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var normalized = settings with { ManifestUrl = settings.ManifestUrl?.Trim() ?? "" };
        if (!string.IsNullOrEmpty(normalized.ManifestUrl)) ValidateHttpsUrl(normalized.ManifestUrl);
        Directory.CreateDirectory(_dataDirectory);
        var temporary = _settingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(normalized, JsonOptions));
            File.Move(temporary, _settingsPath, overwrite: true);
            Settings = normalized;
        }
        finally { TryDelete(temporary); }
    }

    public async Task<UpdateRelease?> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (!HasUpdateSource) return null;
        var source = ValidateHttpsUrl(Settings.ManifestUrl).AbsoluteUri;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var response = await GetAsync(new Uri(source), timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new InvalidOperationException("更新源尚未发布版本，请稍后重试。");
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaximumManifestBytes)
                throw new InvalidDataException("更新清单过大。");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var body = new MemoryStream();
            await CopyLimitedAsync(stream, body, MaximumManifestBytes, null, null, timeout.Token).ConfigureAwait(false);
            var manifest = JsonSerializer.Deserialize<Manifest>(body.ToArray(), JsonOptions)
                ?? throw new InvalidDataException("更新清单为空。");
            if (!Version.TryParse(manifest.Version, out var version))
                throw new InvalidDataException("更新清单中的版本号无效。");
            version = Normalize(version);
            if (version <= _currentVersion) return null;
            ValidateHttpsUrl(manifest.DownloadUrl);
            if (string.IsNullOrEmpty(manifest.Sha256) || manifest.Sha256.Length != 64 || !manifest.Sha256.All(Uri.IsHexDigit))
                throw new InvalidDataException("更新清单缺少有效的 SHA256 校验值。");
            if (manifest.Size is <= 0 or > MaximumDownloadBytes)
                throw new InvalidDataException("更新文件大小无效或超过 200 MB。");
            EnsureSourceUnchanged(source);
            return new UpdateRelease(version.Revision > 0 ? version.ToString(4) : version.ToString(3),
                manifest.DownloadUrl, manifest.Sha256.ToUpperInvariant(), manifest.Size,
                manifest.Notes?.Length > 4000 ? manifest.Notes[..4000] : manifest.Notes ?? "", source);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("检查更新超时，请稍后重试。"); }
        catch (JsonException exception)
        { throw new InvalidDataException("更新清单格式错误。", exception); }
    }

    public async Task<DownloadedUpdate> DownloadAsync(UpdateRelease release,
        IProgress<UpdateProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        EnsureSourceUnchanged(release.SourceUrl);
        if (!Version.TryParse(release.Version, out var version) || Normalize(version) <= _currentVersion)
            throw new InvalidDataException("只能安装比当前版本更新的版本。");
        if (release.Sha256.Length != 64 || !release.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("更新校验值无效。");
        if (release.Size is <= 0 or > MaximumDownloadBytes)
            throw new InvalidDataException("更新文件大小无效。");
        var url = ValidateHttpsUrl(release.DownloadUrl);
        var directory = Path.Combine(_dataDirectory, "downloads");
        Directory.CreateDirectory(directory);
        var cachedPath = Path.Combine(directory, "update-" + release.Sha256.ToUpperInvariant() + ".exe");
        var path = Path.Combine(directory, "download-" + Guid.NewGuid().ToString("N") + ".part");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        try
        {
            if (File.Exists(cachedPath))
            {
                try
                {
                    if (release.Size.HasValue && new FileInfo(cachedPath).Length != release.Size)
                        throw new InvalidDataException("缓存大小不符。");
                    await ValidateDownloadedFileAsync(cachedPath, release.Sha256, timeout.Token).ConfigureAwait(false);
                    EnsureSourceUnchanged(release.SourceUrl);
                    var cachedSize = new FileInfo(cachedPath).Length;
                    progress?.Report(new UpdateProgress(cachedSize, cachedSize));
                    return new DownloadedUpdate(release, cachedPath);
                }
                catch (InvalidDataException) { /* Replace corrupt cache only after a complete verified download. */ }
            }
            using var response = await GetAsync(url, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var length = response.Content.Headers.ContentLength;
            if (length is > MaximumDownloadBytes || (release.Size.HasValue && length.HasValue && release.Size != length))
                throw new InvalidDataException("更新文件大小与清单不符或超过 200 MB。");
            await using (var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var received = await CopyLimitedAsync(input, output, MaximumDownloadBytes,
                    release.Size ?? length, progress, timeout.Token).ConfigureAwait(false);
                if (release.Size.HasValue && received != release.Size)
                    throw new InvalidDataException("更新文件下载不完整。");
            }
            await ValidateDownloadedFileAsync(path, release.Sha256, timeout.Token).ConfigureAwait(false);
            EnsureSourceUnchanged(release.SourceUrl);
            File.Move(path, cachedPath, overwrite: true);
            return new DownloadedUpdate(release, cachedPath);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { TryDelete(path); throw new TimeoutException("下载更新超时，请稍后重试。"); }
        catch { TryDelete(path); throw; }
    }

    /// <summary>After this returns successfully the caller must shut down the application.</summary>
    public void LaunchInstaller(DownloadedUpdate update)
        => LaunchInstallerAsync(update).GetAwaiter().GetResult();

    /// <summary>Confirms the helper is ready before allowing the caller to close.</summary>
    public Task LaunchInstallerAsync(DownloadedUpdate update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        EnsureSourceUnchanged(update.Release.SourceUrl);
        if (!Version.TryParse(update.Release.Version, out var version) || Normalize(version) <= _currentVersion)
            throw new InvalidDataException("此更新版本已过期。");
        return UpdateInstaller.LaunchAsync(update, _dataDirectory, cancellationToken);
    }

    private UpdateSettings LoadSettings()
    {
        try
        {
            if (new FileInfo(_settingsPath) is { Exists: true, Length: <= MaximumManifestBytes })
            {
                var settings = JsonSerializer.Deserialize<UpdateSettings>(File.ReadAllText(_settingsPath), JsonOptions);
                if (settings is not null)
                {
                    if (!string.IsNullOrWhiteSpace(settings.ManifestUrl)) ValidateHttpsUrl(settings.ManifestUrl);
                    return settings with { ManifestUrl = settings.ManifestUrl?.Trim() ?? "" };
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { /* Corrupt local preferences must not prevent the application starting. */ }
        return new UpdateSettings();
    }

    private void EnsureSourceUnchanged(string source)
    {
        if (!HasUpdateSource || ValidateHttpsUrl(Settings.ManifestUrl).AbsoluteUri != source)
            throw new InvalidOperationException("更新来源已改变，请重新检查更新。");
    }

    internal static Uri ValidateHttpsUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("更新地址必须是有效的 HTTPS 链接，且不能包含登录信息或片段。");
        return uri;
    }

    private async Task<HttpResponseMessage> GetAsync(Uri url, CancellationToken token)
    {
        for (var hop = 0; hop < 8; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("ETS2DayNight/" + CurrentVersion);
            var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308))
            {
                if (response.RequestMessage?.RequestUri is { } finalUrl)
                {
                    try { ValidateHttpsUrl(finalUrl.AbsoluteUri); }
                    catch { response.Dispose(); throw; }
                }
                return response;
            }
            var location = response.Headers.Location;
            response.Dispose();
            if (location is null) throw new InvalidDataException("更新服务器返回了无效的跳转地址。");
            url = ValidateHttpsUrl(new Uri(url, location).AbsoluteUri);
        }
        throw new InvalidDataException("更新服务器跳转次数过多。");
    }

    internal static async Task ValidateDownloadedFileAsync(string path, string expectedHash, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        if (stream.Length is < 64 or > MaximumDownloadBytes)
            throw new InvalidDataException("下载的文件不是有效的 Windows 程序。");
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
        if (!string.Equals(hash, expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("更新文件 SHA256 校验失败，已取消更新。");
        stream.Position = 0;
        var header = new byte[64];
        await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        var offset = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(60));
        if (header[0] != 'M' || header[1] != 'Z' || offset < 64 || offset > stream.Length - 6)
            throw new InvalidDataException("下载的文件不是有效的 Windows 程序。");
        stream.Position = offset;
        var pe = new byte[6];
        await stream.ReadExactlyAsync(pe, token).ConfigureAwait(false);
        if (BinaryPrimitives.ReadUInt32LittleEndian(pe) != 0x4550 || BinaryPrimitives.ReadUInt16LittleEndian(pe.AsSpan(4)) != 0x8664)
            throw new InvalidDataException("更新文件必须为 Windows x64 程序。");
    }

    private static async Task<long> CopyLimitedAsync(Stream input, Stream output, long maximum,
        long? length, IProgress<UpdateProgress>? progress, CancellationToken token)
    {
        var buffer = new byte[81920];
        long received = 0;
        long nextProgressAt = 0;
        int count;
        while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            received += count;
            if (received > maximum || (length.HasValue && received > length.Value))
                throw new InvalidDataException("下载文件超过允许的大小。");
            await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
            if (Environment.TickCount64 >= nextProgressAt || received == length)
            {
                progress?.Report(new UpdateProgress(received, length));
                nextProgressAt = Environment.TickCount64 + 100;
            }
        }
        progress?.Report(new UpdateProgress(received, length));
        return received;
    }

    private static Version Normalize(Version version) => new(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));
    internal static void TryDelete(string path) { try { File.Delete(path); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { } }
    public void Dispose() { if (_ownsClient) _client.Dispose(); }

    private sealed class Manifest
    {
        public string Version { get; init; } = "";
        public string DownloadUrl { get; init; } = "";
        public string Sha256 { get; init; } = "";
        public long? Size { get; init; }
        public string Notes { get; init; } = "";
    }
}
