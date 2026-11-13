using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

namespace ETS2DayNightModern;

public partial class MainWindow
{
    private readonly UpdateService _updates;
    private readonly CancellationTokenSource _updateLifetime = new();
    private readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromHours(6) };
    private CancellationTokenSource? _updateOperation;
    private UpdateRelease? _availableUpdate;
    private DownloadedUpdate? _downloadedUpdate;
    private bool _updateBusy;
    private bool _updatesClosing;
    private bool _downloadProgressActive;
    private bool _loadingUpdateSettings;
    private readonly HashSet<string> _promptedUpdateVersions = new(StringComparer.Ordinal);
    private IInputElement? _updatePreviousFocus;

    private void InitializeUpdater()
    {
        UpdateVersionText.Text = $"当前版本 v{_updates.CurrentVersion}";
        UpdateStatusText.Text = "启动后会自动检查新版本。";
        LoadUpdateSettings();
        AutoDownloadUpdates.PropertyChanged += (_, args) =>
        {
            if (args.Property != ToggleButton.IsCheckedProperty || _loadingUpdateSettings) return;
            try
            {
                _updates.SaveSettings(_updates.Settings with { AutoDownload = AutoDownloadUpdates.IsChecked == true });
            }
            catch (Exception error) { UpdateStatusText.Text = error.Message; }
        };
        _updateTimer.Tick += async (_, _) =>
        {
            if (_updates.Settings.AutoCheck && _downloadedUpdate is null)
                await CheckForUpdatesAsync();
        };
        Opened += async (_, _) =>
        {
            _updateTimer.Start();
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), _updateLifetime.Token);
                if (_updates.Settings.AutoCheck) await CheckForUpdatesAsync();
            }
            catch (OperationCanceledException) { }
        };
        Closed += (_, _) =>
        {
            _updatesClosing = true;
            _updateTimer.Stop();
            _updateLifetime.Cancel();
            _updates.Dispose();
        };
    }

    private void LoadUpdateSettings()
    {
        _loadingUpdateSettings = true;
        try
        {
            UpdateSourceText.Text = _updates.Settings.ManifestUrl;
            AutoCheckUpdates.IsChecked = _updates.Settings.AutoCheck;
            AutoDownloadUpdates.IsChecked = _updates.Settings.AutoDownload;
        }
        finally { _loadingUpdateSettings = false; }
    }

    private void Update_Click(object? sender, RoutedEventArgs e)
    {
        if (UpdateOverlay.IsVisible)
        {
            HideUpdates();
            return;
        }
        ShowUpdatesPanel();
    }

    private void ShowUpdatesPanel()
    {
        if (_updatesClosing || UpdateOverlay.IsVisible) return;
        EndSliderDrag();
        LoadUpdateSettings();
        _updatePreviousFocus = FocusManager?.GetFocusedElement();
        UpdateOverlay.IsVisible = true;
        if (_updateBusy)
            this.FindControl<Button>("HideUpdatesButton")?.Focus();
        else
            CheckUpdatesButton.Focus();
    }

    private void HideUpdates_Click(object? sender, RoutedEventArgs e) => HideUpdates();

    private void HideUpdates()
    {
        UpdateOverlay.IsVisible = false;
        if (_updatePreviousFocus is Control { IsEffectivelyVisible: true } previous)
            previous.Focus();
        else
            UpdateButton.Focus();
    }

    private async void CheckUpdates_Click(object? sender, RoutedEventArgs e)
    {
        if (_updateBusy) return;
        try
        {
            var source = UpdateSourceText.Text?.Trim() ?? "";
            var sourceChanged = !string.Equals(source, _updates.Settings.ManifestUrl, StringComparison.Ordinal);
            _updates.SaveSettings(new UpdateSettings
            {
                ManifestUrl = source,
                AutoCheck = AutoCheckUpdates.IsChecked == true,
                AutoDownload = AutoDownloadUpdates.IsChecked == true
            });
            if (sourceChanged)
            {
                _availableUpdate = null;
                _downloadedUpdate = null;
                ResetUpdateAnnouncement();
            }
        }
        catch (Exception error)
        {
            UpdateStatusText.Text = error.Message;
            return;
        }
        await CheckForUpdatesAsync();
    }

    private async Task CheckForUpdatesAsync()
    {
        if (_updateBusy || _updatesClosing) return;
        if (!_updates.HasUpdateSource)
        {
            UpdateStatusText.Text = "尚未设置更新源，请联系发布者。";
            return;
        }
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_updateLifetime.Token);
        _updateOperation = operation;
        SetUpdateBusy(true);
        UpdateStatusText.Text = "正在检查更新…";
        UpdateNotesText.IsVisible = false;
        UpdateProgressBar.IsVisible = true;
        UpdateProgressBar.IsIndeterminate = true;
        try
        {
            var release = await _updates.CheckAsync(operation.Token);
            if (_updatesClosing) return;
            _availableUpdate = release;
            if (release is null)
            {
                _downloadedUpdate = null;
                ResetUpdateAnnouncement();
                UpdateStatusText.Text = "当前为最新版本。";
                return;
            }
            ShowUpdateNotes(release);
            if (_downloadedUpdate?.Release == release && File.Exists(_downloadedUpdate.FilePath))
            {
                MarkUpdateReady();
                PromptForUpdate(release);
                return;
            }
            _downloadedUpdate = null;
            ResetUpdateAnnouncement();
            UpdateStatusText.Text = $"发现新版本 v{release.Version}。";
            PromptForUpdate(release);
            if (_updates.Settings.AutoDownload)
                await DownloadUpdateCoreAsync(release, operation.Token);
        }
        catch (OperationCanceledException)
        {
            if (!_updatesClosing) UpdateStatusText.Text = "已取消更新，稍后可以重试。";
        }
        catch (Exception error)
        {
            // Background errors stay in this panel and never interrupt a drive.
            if (!_updatesClosing) UpdateStatusText.Text = error.Message;
        }
        finally
        {
            _updateOperation = null;
            if (!_updatesClosing)
            {
                UpdateProgressBar.IsVisible = false;
                SetUpdateBusy(false);
            }
        }
    }

    private void SetUpdateBusy(bool busy)
    {
        _updateBusy = busy;
        UpdateSettingsFields.IsEnabled = !busy;
        CheckUpdatesButton.IsEnabled = !busy;
        CancelUpdateButton.IsVisible = busy;
        InstallUpdateButton.IsVisible = _availableUpdate is not null;
        InstallUpdateButton.IsEnabled = !busy;
        InstallUpdateButton.Content = _downloadedUpdate is null ? "下载更新" : "重启更新";
    }

    private void PromptForUpdate(UpdateRelease release)
    {
        // A dismissed version stays quiet for this run; newer versions can notify again.
        if (_promptedUpdateVersions.Add(release.Version))
            ShowUpdatesPanel();
    }

    private void ShowUpdateNotes(UpdateRelease release)
    {
        UpdateNotesText.Text = release.Notes;
        UpdateNotesText.IsVisible = !string.IsNullOrWhiteSpace(release.Notes);
    }

    private async Task DownloadUpdateCoreAsync(UpdateRelease release, CancellationToken cancellationToken)
    {
        UpdateStatusText.Text = $"正在下载 v{release.Version}…";
        _downloadProgressActive = true;
        UpdateProgressBar.IsVisible = true;
        UpdateProgressBar.IsIndeterminate = true;
        var progress = new Progress<UpdateProgress>(value =>
        {
            if (!_downloadProgressActive || _updatesClosing || cancellationToken.IsCancellationRequested) return;
            var total = value.TotalBytes;
            UpdateProgressBar.IsIndeterminate = total is null or <= 0;
            if (total is > 0)
            {
                var percent = Math.Clamp(100d * value.BytesReceived / total.Value, 0, 100);
                UpdateProgressBar.Value = percent;
                UpdateStatusText.Text = $"正在下载 v{release.Version} · {percent:0}%";
            }
            else
                UpdateStatusText.Text = $"正在下载 v{release.Version} · {value.BytesReceived / 1048576d:0.0} MB";
        });
        try
        {
            var downloaded = await _updates.DownloadAsync(release, progress, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _downloadedUpdate = downloaded;
            MarkUpdateReady();
        }
        finally { _downloadProgressActive = false; }
    }

    private void ResetUpdateAnnouncement()
    {
        UpdateButtonText.Text = "软件更新";
        UpdateButton.ClearValue(ForegroundProperty);
        ToolTip.SetTip(UpdateButton, "软件更新");
        AutomationProperties.SetName(UpdateButton, "软件更新");
    }

    private void MarkUpdateReady()
    {
        if (_downloadedUpdate is null) return;
        UpdateStatusText.Text = $"v{_downloadedUpdate.Release.Version} 已下载并通过校验，可以重启更新。";
        UpdateButtonText.Text = "重启更新";
        UpdateButton.Foreground = new SolidColorBrush(Color.Parse("#BDA2ED"));
        ToolTip.SetTip(UpdateButton, UpdateStatusText.Text);
        AutomationProperties.SetName(UpdateButton, UpdateStatusText.Text);
        InstallUpdateButton.Content = "重启更新";
    }

    private void CancelUpdate_Click(object? sender, RoutedEventArgs e) => _updateOperation?.Cancel();

    private async void InstallUpdate_Click(object? sender, RoutedEventArgs e)
    {
        if (_updateBusy || _availableUpdate is null) return;
        if (_downloadedUpdate is not null)
        {
            using var installation = CancellationTokenSource.CreateLinkedTokenSource(_updateLifetime.Token);
            _updateOperation = installation;
            SetUpdateBusy(true);
            UpdateStatusText.Text = "正在准备安装…";
            try
            {
                await _updates.LaunchInstallerAsync(_downloadedUpdate, installation.Token);
                Close();
            }
            catch (OperationCanceledException)
            {
                if (!_updatesClosing) UpdateStatusText.Text = "已取消安装，当前版本继续运行。";
            }
            catch (Exception error)
            {
                UpdateStatusText.Text = error.Message;
                if (error is FileNotFoundException)
                {
                    _downloadedUpdate = null;
                    ResetUpdateAnnouncement();
                    SetUpdateBusy(false);
                }
            }
            finally
            {
                _updateOperation = null;
                if (!_updatesClosing) SetUpdateBusy(false);
            }
            return;
        }
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_updateLifetime.Token);
        _updateOperation = operation;
        SetUpdateBusy(true);
        try
        {
            await DownloadUpdateCoreAsync(_availableUpdate, operation.Token);
        }
        catch (OperationCanceledException)
        {
            if (!_updatesClosing) UpdateStatusText.Text = "已取消下载，稍后可以重试。";
        }
        catch (Exception error)
        {
            if (!_updatesClosing) UpdateStatusText.Text = error.Message;
        }
        finally
        {
            _updateOperation = null;
            if (!_updatesClosing)
            {
                UpdateProgressBar.IsVisible = false;
                SetUpdateBusy(false);
            }
        }
    }
}
