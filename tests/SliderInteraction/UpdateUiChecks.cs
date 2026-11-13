using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using ETS2DayNightModern;

internal static partial class Program
{
    private static void UpdateOverlayCloses(Fixture f)
    {
        Click(f, "UpdateButton");
        if (!f.Window.FindControl<Border>("UpdateOverlay")!.IsVisible)
            throw new InvalidOperationException("update panel did not open");
        var value = f.Slider.Value;
        f.Window.MouseDown(f.At(.2), MouseButton.Left);
        f.Window.MouseUp(f.At(.2), MouseButton.Left);
        Near(value, f.Slider.Value, "update panel let the background slider receive a click");
        f.Window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        f.Window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        if (f.Window.FindControl<Border>("UpdateOverlay")!.IsVisible || !f.Window.IsVisible)
            throw new InvalidOperationException("Escape did not close only the panel");
        Equal(0, f.Service.AppliedMinutes.Count, "update panel changed the game");
    }

    private static void UpdateSettingsSave(Fixture f)
    {
        Click(f, "UpdateButton");
        f.Window.FindControl<TextBox>("UpdateSourceText")!.Text = "https://example.com/update.json";
        f.Window.FindControl<CheckBox>("AutoCheckUpdates")!.IsChecked = false;
        f.Window.FindControl<CheckBox>("AutoDownloadUpdates")!.IsChecked = false;
        Click(f, "CheckUpdatesButton");
        WaitFor(() => f.Window.FindControl<TextBlock>("UpdateStatusText")!.Text == "当前为最新版本。", "manual update check did not finish");
        if (f.UpdateHandler.LastManifestUrl != "https://example.com/update.json")
            throw new InvalidOperationException("manual check did not use saved source");
        Click(f, "UpdateButton");
        Click(f, "UpdateButton");
        if (f.Window.FindControl<CheckBox>("AutoCheckUpdates")!.IsChecked != false ||
            f.Window.FindControl<CheckBox>("AutoDownloadUpdates")!.IsChecked != false)
            throw new InvalidOperationException("preferences were not retained");
    }

    private static void UpdateReady(Fixture f)
    {
        f.UpdateHandler.ReleaseVersion = "1.2.0";
        Click(f, "UpdateButton");
        Click(f, "CheckUpdatesButton");
        WaitFor(() => f.Window.FindControl<Button>("InstallUpdateButton")!.IsEnabled &&
            f.Window.FindControl<Button>("InstallUpdateButton")!.Content?.ToString() == "重启更新", "downloaded update was not ready");
        if (f.Window.FindControl<TextBlock>("UpdateButtonText")!.Text != "重启更新")
            throw new InvalidOperationException("ready update was not announced in the title bar");
        Equal(1, f.UpdateHandler.Downloads, "update was not downloaded exactly once");
        var downloaded = (DownloadedUpdate)typeof(MainWindow).GetField("_downloadedUpdate",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(f.Window)!;
        File.Delete(downloaded.FilePath);
        Click(f, "CheckUpdatesButton");
        WaitFor(() => f.UpdateHandler.Downloads == 2 && f.Window.FindControl<Button>("InstallUpdateButton")!.IsEnabled,
            "missing cache could not be downloaded again");
        Equal(0, f.Service.AppliedMinutes.Count, "software update touched the game");
    }

    private static void UnpublishedUpdateSource(Fixture f)
    {
        f.UpdateHandler.Unpublished = true;
        Click(f, "UpdateButton");
        Click(f, "CheckUpdatesButton");
        WaitFor(() => f.Window.FindControl<Button>("CheckUpdatesButton")!.IsEnabled, "failed check left UI busy");
        if (f.Window.FindControl<TextBlock>("UpdateStatusText")!.Text?.Contains("无法获取更新信息") != true)
            throw new InvalidOperationException("missing release did not explain the failure");
        if (f.Window.FindControl<Button>("InstallUpdateButton")!.IsVisible)
            throw new InvalidOperationException("missing release offered an install");
        Click(f, "UpdateButton");
        TrackClick(f);
    }

    private static void UpdateCheckRetry(Fixture f)
    {
        f.UpdateHandler.Unpublished = true;
        Click(f, "UpdateButton");
        Click(f, "CheckUpdatesButton");
        WaitFor(() => f.Window.FindControl<Button>("CheckUpdatesButton")!.IsEnabled, "failed update check stayed busy");
        if (f.Window.FindControl<TextBlock>("UpdateStatusText")!.Text == "当前为最新版本。")
            throw new InvalidOperationException("network failure was reported as latest version");
        f.UpdateHandler.Unpublished = false;
        Click(f, "CheckUpdatesButton");
        WaitFor(() => f.Window.FindControl<TextBlock>("UpdateStatusText")!.Text == "当前为最新版本。", "retry did not clear old update error");
        if (f.Window.FindControl<Button>("InstallUpdateButton")!.IsVisible ||
            f.Window.FindControl<ProgressBar>("UpdateProgressBar")!.IsVisible)
            throw new InvalidOperationException("current version still offered an install or loading progress");
    }

    private static void UpdateButtonAlignment(Fixture f)
    {
        f.UpdateHandler.ReleaseVersion = "1.2.0";
        Click(f, "UpdateButton");
        Click(f, "CheckUpdatesButton");
        WaitFor(() => f.Window.FindControl<Button>("InstallUpdateButton")!.IsEnabled &&
            f.Window.FindControl<Button>("InstallUpdateButton")!.Content?.ToString() == "重启更新", "update button alignment fixture did not finish downloading");
        f.Window.UpdateLayout();
        foreach (var name in new[] { "CheckUpdatesButton", "InstallUpdateButton" })
        {
            var button = f.Window.FindControl<Button>(name)!;
            var label = button.GetVisualDescendants().OfType<TextBlock>().Single();
            var origin = label.TranslatePoint(default, button)!.Value;
            var dx = origin.X + label.Bounds.Width / 2 - button.Bounds.Width / 2;
            var dy = origin.Y + label.Bounds.Height / 2 - button.Bounds.Height / 2;
            if (Math.Abs(dx) > 1 || Math.Abs(dy) > 1)
                throw new InvalidOperationException($"{name} label is not centered: offset {dx:F1}, {dy:F1}");
        }
    }

    public sealed class UpdateHttpStub : HttpMessageHandler
    {
        public string ReleaseVersion { get; set; } = "1.1.2";
        public bool Unpublished { get; set; }
        public string? LastManifestUrl { get; private set; }
        public int Downloads { get; private set; }
        private readonly byte[] _executable = MakeTestExecutable();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.RequestUri!.AbsolutePath.EndsWith(".exe", StringComparison.Ordinal))
            {
                Downloads++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_executable) });
            }
            LastManifestUrl = request.RequestUri.AbsoluteUri;
            if (Unpublished) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            var json = JsonSerializer.Serialize(new
            {
                version = ReleaseVersion,
                downloadUrl = "https://example.com/update.exe",
                sha256 = Convert.ToHexString(SHA256.HashData(_executable)),
                size = _executable.Length,
                notes = "界面动画与软件更新。"
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }

        private static byte[] MakeTestExecutable()
        {
            var data = new byte[256];
            data[0] = (byte)'M'; data[1] = (byte)'Z';
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(60), 128);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(128), 0x4550);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(132), 0x8664);
            return data;
        }
    }
}
