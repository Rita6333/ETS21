using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using System.Globalization;
using System.Runtime.InteropServices;

namespace ETS2DayNightModern;

public partial class MainWindow : Window
{
    private readonly GameTimeService _service = new();
    private readonly DispatcherTimer _timer;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly string[] _startupArgs;
    private IPointer? _sliderPointer;
    private int _selectedMinute = 720;
    private int _messageVersion;
    private GameState _state = GameState.Empty;

    public MainWindow() : this(Array.Empty<string>()) { }

    public MainWindow(string[] startupArgs) : this(startupArgs, new UpdateService()) { }

    internal MainWindow(string[] startupArgs, UpdateService updates)
    {
        _updates = updates;
        _startupArgs = startupArgs;
        InitializeComponent();
        InitializeAnimations();
        InitializeUpdater();
        InitializeUpdateRelaunch();
        PropertyChanged += (_, args) =>
        {
            if (args.Property == WindowStateProperty || args.Property == CanResizeProperty)
                UpdateWindowControls();
        };
        AddHandler(KeyDownEvent, Window_KeyDown, RoutingStrategies.Tunnel);
        UpdateWindowControls();
        // Own the whole pointer gesture. Letting the track also handle the
        // press leaves its drag active when release goes to our capture.
        TimeSlider.AddHandler(InputElement.PointerPressedEvent,
            Slider_PointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        TimeSlider.AddHandler(InputElement.PointerReleasedEvent,
            Slider_PointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        TimeSlider.AddHandler(InputElement.PointerMovedEvent,
            Slider_PointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        TimeSlider.AddHandler(InputElement.PointerCaptureLostEvent,
            Slider_PointerCaptureLost, RoutingStrategies.Direct, handledEventsToo: true);
        TimeSlider.PropertyChanged += (_, args) =>
        {
            if (args.Property == Slider.ValueProperty)
            {
                _selectedMinute = (int)Math.Round(TimeSlider.Value);
                SelectedTimeText.Text = FormatMinute(_selectedMinute);
                UpdateSelectionAppearance();
            }
        };
        UpdateSelectionAppearance();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += async (_, _) => await RefreshAsync();
        Opened += async (_, _) =>
        {
            await RefreshAsync();
            _timer.Start();
            await RunStartupActionAsync();
        };
        Deactivated += (_, _) => EndSliderDrag();
        Closed += (_, _) =>
        {
            EndSliderDrag();
            _timer.Stop();
        };
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var properties = e.GetCurrentPoint(this).Properties;
        if (properties.PointerUpdateKind == PointerUpdateKind.RightButtonPressed)
        {
            e.Handled = ShowSystemMenu(e.GetPosition(this));
            return;
        }
        if (properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed) return;
        e.Handled = true;
        if (e.ClickCount == 2)
            ToggleMaximize();
        else
            BeginMoveDrag(e);
    }

    private void Minimize_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object? sender, RoutedEventArgs e) => ToggleMaximize();

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize()
    {
        if (!CanResize || WindowState == WindowState.FullScreen) return;
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void UpdateWindowControls()
    {
        var maximized = WindowState == WindowState.Maximized;
        MaximizeButton.IsEnabled = CanResize && WindowState != WindowState.FullScreen;
        MaximizeIcon.Data = Geometry.Parse(maximized ? "M2 5H9V12H2Z M5 5V2H12V9H9" : "M2 2H12V12H2Z");
        var label = maximized ? "还原" : "最大化";
        ToolTip.SetTip(MaximizeButton, label);
        AutomationProperties.SetName(MaximizeButton, label);
        WindowFrame.BorderThickness = new Thickness(maximized ? 0 : 1);
        var square = WindowState is WindowState.Maximized or WindowState.FullScreen;
        WindowFrame.CornerRadius = new CornerRadius(square ? 0 : 8);
        WindowTitleBar.CornerRadius = square ? new CornerRadius(0) : new CornerRadius(8, 8, 0, 0);
    }

    private void Window_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && UpdateOverlay.IsVisible)
        {
            HideUpdates();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.F4 && e.KeyModifiers == KeyModifiers.Alt)
        {
            Close();
            e.Handled = true;
        }
        else if (e.Key == Key.Space && e.KeyModifiers == KeyModifiers.Alt)
            e.Handled = ShowSystemMenu(new Point(0, 58));
    }

    private bool ShowSystemMenu(Point position)
    {
        var handle = TryGetPlatformHandle();
        if (!OperatingSystem.IsWindows() || handle?.HandleDescriptor != "HWND") return false;
        var menu = GetSystemMenu(handle.Handle, false);
        if (menu == 0) return false;
        var normal = WindowState == WindowState.Normal;
        EnableMenuItem(menu, 0xF120, normal ? 1u : 0u); // Restore
        EnableMenuItem(menu, 0xF010, normal ? 0u : 1u); // Move
        EnableMenuItem(menu, 0xF000, normal && CanResize ? 0u : 1u); // Size
        EnableMenuItem(menu, 0xF030, normal && CanResize ? 0u : 1u); // Maximize
        EnableMenuItem(menu, 0xF060, 0); // NoChrome disables the native caption close command.
        var screen = this.PointToScreen(position);
        var command = TrackPopupMenuEx(menu, 0x0102, screen.X, screen.Y, handle.Handle, 0);
        if (command != 0)
            PostMessage(handle.Handle, 0x0112, (nint)command, 0);
        return true;
    }

    [DllImport("user32.dll")]
    private static extern nint GetSystemMenu(nint window, [MarshalAs(UnmanagedType.Bool)] bool revert);
    [DllImport("user32.dll")]
    private static extern uint EnableMenuItem(nint menu, uint item, uint flags);
    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint window, nint parameters);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint window, uint message, nint wparam, nint lparam);

    private async Task RefreshAsync()
    {
        var state = await Task.Run(_service.Probe);
        _state = state;
        GameTimeText.Text = state.GameMinute >= 0 ? FormatMinute(state.GameMinute) : "--:--";
        EnvironmentText.Text = state.EnvironmentMinute >= 0
            ? $"环境：已锁定 {FormatMinute(state.EnvironmentMinute)}"
            : state.Alive ? "环境：原样" : "环境：—";
        if (_sliderPointer is null)
        {
            var value = state.EnvironmentMinute >= 0 ? state.EnvironmentMinute : state.GameMinute;
            if (value >= 0)
            {
                _selectedMinute = value;
                TimeSlider.Value = value;
                SelectedTimeText.Text = FormatMinute(value);
            }
        }
    }

    private void UpdateSelectionAppearance()
    {
        SelectionPeriodText.Text = _selectedMinute switch
        {
            >= 300 and < 480 => "清晨 · MORNING",
            >= 480 and < 1080 => "白天 · DAYTIME",
            >= 1080 and < 1200 => "黄昏 · SUNSET",
            _ => "夜晚 · NIGHT"
        };
        foreach (var button in new[] { PresetMorning, PresetDay, PresetDusk, PresetNight })
            button.Classes.Set("selected", button.Tag?.ToString() ==
                _selectedMinute.ToString(CultureInfo.InvariantCulture));
    }

    private void Slider_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
        var properties = e.GetCurrentPoint(TimeSlider).Properties;
        if (_sliderPointer is not null ||
            properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed ||
            !properties.IsLeftButtonPressed) return;

        e.PreventGestureRecognition();
        _sliderPointer = e.Pointer;
        e.Pointer.Capture(TimeSlider);
        UpdateSliderFromPointer(e);
    }

    private void Slider_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_sliderPointer != e.Pointer) return;
        e.Handled = true;
        if (e.Pointer.Captured != TimeSlider ||
            !e.GetCurrentPoint(TimeSlider).Properties.IsLeftButtonPressed)
        {
            EndSliderDrag();
            return;
        }
        UpdateSliderFromPointer(e);
    }

    private async void Slider_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        e.Handled = true;
        if (_sliderPointer != e.Pointer ||
            e.GetCurrentPoint(TimeSlider).Properties.PointerUpdateKind !=
                PointerUpdateKind.LeftButtonReleased) return;

        UpdateSliderFromPointer(e);
        EndSliderDrag();
        await ApplyAsync(_selectedMinute);
    }

    private void Slider_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_sliderPointer == e.Pointer)
            EndSliderDrag();
    }

    private void EndSliderDrag()
    {
        var pointer = _sliderPointer;
        _sliderPointer = null;
        if (pointer?.Captured == TimeSlider)
            pointer.Capture(null);
    }

    private void UpdateSliderFromPointer(PointerEventArgs e)
    {
        var width = TimeSlider.Bounds.Width;
        if (width <= 0) return;
        var x = e.GetCurrentPoint(TimeSlider).Position.X;
        var value = Math.Clamp(x / width, 0, 1) * (TimeSlider.Maximum - TimeSlider.Minimum)
                    + TimeSlider.Minimum;
        TimeSlider.Value = value;
    }

    private async void Preset_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is null) return;
        if (!int.TryParse(button.Tag.ToString(), NumberStyles.Integer,
                          CultureInfo.InvariantCulture, out var minute)) return;
        _selectedMinute = minute;
        TimeSlider.Value = minute;
        SelectedTimeText.Text = FormatMinute(minute);
        await ApplyAsync(minute);
    }

    private async void Restore_Click(object? sender, RoutedEventArgs e)
    {
        if (_state.Alive && _state.AccessDenied && OperatingSystem.IsWindows() && !Elevation.IsAdministrator())
        {
            EnsureAdministrator("--restore");
            return;
        }
        if (!_state.Alive || !_state.Patchable)
        {
            SetStatus("请先启动支持的 ETS2 版本。", "#DEBB7D");
            return;
        }
        if (!EnsureAdministrator("--restore")) return;
        await _operationGate.WaitAsync();
        try
        {
            SetStatus("正在恢复游戏原始代码…", "#A09DA7");
            var result = await Task.Run(() => _service.Restore(_state));
            SetStatus(result.Message, result.Success ? "#71D5A5" : "#EF999E");
            await RefreshAsync();
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task ApplyAsync(int minute)
    {
        if (_state.Alive && _state.AccessDenied && OperatingSystem.IsWindows() && !Elevation.IsAdministrator())
        {
            EnsureAdministrator($"--apply {minute}");
            return;
        }
        if (!_state.Alive || !_state.Patchable)
        {
            SetStatus("请先启动支持的 ETS2 版本。", "#DEBB7D");
            return;
        }
        if (!EnsureAdministrator($"--apply {minute}")) return;
        await _operationGate.WaitAsync();
        try
        {
            SetStatus("正在切换环境…", "#A09DA7");
            var result = await Task.Run(() => _service.Apply(_state, minute));
            SetStatus(result.Message, result.Success ? "#71D5A5" : "#EF999E");
            await RefreshAsync();
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private void SetStatus(string message, string color)
    {
        var version = ++_messageVersion;
        InlineMessage.IsVisible = color is "#EF999E" or "#DEBB7D";
        if (!InlineMessage.IsVisible) return;
        InlineMessageText.Text = message;
        InlineMessageText.Foreground = new SolidColorBrush(Color.Parse(color));
        DispatcherTimer.RunOnce(() =>
        {
            if (version == _messageVersion)
                InlineMessage.IsVisible = false;
        }, TimeSpan.FromSeconds(8));
    }

    private static string FormatMinute(int minute) => $"{minute / 60:00}:{minute % 60:00}";

    private bool EnsureAdministrator(string arguments)
    {
        if (!OperatingSystem.IsWindows()) return true;
        if (Elevation.IsAdministrator()) return true;
        if (Elevation.TryRelaunch(arguments))
        {
            Close();
            return false;
        }
        SetStatus("需要管理员权限才能修改 ETS2，请右键选择“以管理员身份运行”。", "#EF999E");
        return false;
    }

    private async Task RunStartupActionAsync()
    {
        for (var i = 0; i < _startupArgs.Length; i++)
        {
            if (string.Equals(_startupArgs[i], "--restore", StringComparison.OrdinalIgnoreCase))
            {
                Restore_Click(null, new RoutedEventArgs());
                return;
            }
            if (string.Equals(_startupArgs[i], "--apply", StringComparison.OrdinalIgnoreCase) &&
                i + 1 < _startupArgs.Length && int.TryParse(_startupArgs[i + 1], out var minute))
            {
                await ApplyAsync(Math.Clamp(minute, 0, 1439));
                return;
            }
        }
    }
}
