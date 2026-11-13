using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ETS2DayNightModern;

internal sealed class TestApp : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        var theme = new FluentTheme();
        theme.Palettes[ThemeVariant.Dark] = new ColorPaletteResources { Accent = Avalonia.Media.Color.Parse("#9472D8") };
        Styles.Add(theme);
        Styles.Add(new StyleInclude(new Uri("avares://ETS2昼夜切换/"))
        {
            Source = new Uri("avares://ETS2昼夜切换/Styles/TransportTheme.axaml")
        });
    }
}

internal static partial class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        AppBuilder.Configure<TestApp>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();

        if (args.Length > 0 && args[0] == "--render")
            return RenderPreviews(args.Length > 1 ? args[1] : "_case/ui_previews");

        (string Name, Action<Fixture> Run)[] tests =
        [
            ("track click stops following after release", TrackClick),
            ("track drag commits final release position once", TrackDrag),
            ("thumb drag stops following after release", ThumbDrag),
            ("release outside window clamps and ends drag", OutsideRelease),
            ("capture loss cancels without applying", CaptureLoss),
            ("capture transfer preserves the new owner", CaptureTransfer),
            ("window deactivation cancels without applying", Deactivated),
            ("missing release is cancelled on button-free move", MissingRelease),
            ("right button does not change the track", f => IgnoreButton(f, MouseButton.Right, false)),
            ("right button does not change the thumb", f => IgnoreButton(f, MouseButton.Right, true)),
            ("middle button does not change the track", f => IgnoreButton(f, MouseButton.Middle, false)),
            ("custom maximize button toggles restore and its label", MaximizeToggle),
            ("custom title bar double click toggles maximize", TitleDoubleClick),
            ("fixed-size window ignores maximize", FixedSizeMaximize),
            ("custom minimize button minimizes", MinimizeWindow),
            ("custom close button closes", CloseWindow),
            ("Alt F4 closes the custom window", AltF4),
            ("update overlay opens and Escape returns to the app", UpdateOverlayCloses),
            ("update settings save and manual check uses configured source", UpdateSettingsSave),
            ("background download shows restart and missing cache can download again", UpdateReady),
            ("unpublished update source keeps the app usable", UnpublishedUpdateSource),
            ("failed update check can retry and show current version is latest", UpdateCheckRetry),
            ("update action labels are centered in their buttons", UpdateButtonAlignment),
        ];

        var failures = 0;
        if (args.Length > 0)
            tests = tests.Where(test => test.Name.Contains(args[0], StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach (var test in tests)
        {
            try
            {
                using var fixture = new Fixture();
                test.Run(fixture);
                Console.WriteLine($"PASS {test.Name}");
            }
            catch (Exception error)
            {
                failures++;
                Console.WriteLine($"FAIL {test.Name}: {error.Message}");
            }
        }
        Console.WriteLine($"{tests.Length - failures}/{tests.Length} passed; game access is stubbed.");
        return failures == 0 ? 0 : 1;
    }

    private static int RenderPreviews(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        (string Name, bool Minimum, GameState State)[] scenes =
        [
            ("default", false, new(true, true, 1, 1, 13 * 60 + 48, -1, 1)),
            ("minimum", true, new(true, true, 1, 1, 13 * 60 + 48, -1, 1)),
            ("locked", false, new(true, true, 1, 1, 13 * 60 + 48, 19 * 60, 1)),
            ("waiting", false, GameState.Empty),
            ("updates", false, GameState.Empty),
            ("updates-minimum", true, GameState.Empty),
            ("updates-latest", false, GameState.Empty),
            ("update-ready", false, GameState.Empty),
        ];
        try
        {
            foreach (var scene in scenes)
            {
                GameTimeService.InitialState = scene.State;
                using var fixture = new Fixture(scene.Minimum);
                if (scene.Name.StartsWith("update", StringComparison.Ordinal))
                {
                    Click(fixture, "UpdateButton");
                    if (scene.Name == "update-ready")
                    {
                        fixture.UpdateHandler.ReleaseVersion = "1.2.0";
                        Click(fixture, "CheckUpdatesButton");
                        WaitFor(() => fixture.Window.FindControl<Button>("InstallUpdateButton")!.Content?.ToString() == "重启更新",
                            "update preview was not downloaded");
                    }
                    else if (scene.Name == "updates-latest")
                    {
                        Click(fixture, "CheckUpdatesButton");
                        WaitFor(() => fixture.Window.FindControl<TextBlock>("UpdateStatusText")!.Text == "当前为最新版本。",
                            "latest version preview did not finish checking");
                    }
                }
                PumpFor(TimeSpan.FromMilliseconds(400));
                fixture.Window.MouseMove(new Point(10, 70));
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();
                CheckVisibleControls(fixture.Window);
                using var bitmap = fixture.Window.CaptureRenderedFrame()
                    ?? throw new InvalidOperationException("No rendered frame was returned by Skia.");
                var path = Path.GetFullPath(Path.Combine(outputDirectory, scene.Name + ".png"));
                bitmap.Save(path);
                Console.WriteLine($"RENDER {scene.Name}: {bitmap.PixelSize.Width}x{bitmap.PixelSize.Height} {path}");
            }
            return 0;
        }
        finally
        {
            GameTimeService.InitialState = new(true, true, 1, 1, -1, -1, 1);
        }
    }

    private static void CheckVisibleControls(MainWindow window)
    {
        string[] names = ["TimeSlider", "SelectedTimeText", "GameTimeText", "EnvironmentText",
            "MinimizeButton", "MaximizeButton", "CloseButton"];
        var occupied = new List<(string Name, Rect Bounds)>();
        foreach (var name in names)
        {
            var control = window.FindControl<Control>(name)
                ?? throw new InvalidOperationException($"Required control {name} is missing.");
            var position = control.TranslatePoint(default, window)
                ?? throw new InvalidOperationException($"{name} has no window position.");
            var bounds = new Rect(position, control.Bounds.Size);
            if (bounds.Width <= 0 || bounds.Height <= 0 ||
                bounds.Left < -.5 || bounds.Top < -.5 ||
                bounds.Right > window.Bounds.Width + .5 || bounds.Bottom > window.Bounds.Height + .5)
                throw new InvalidOperationException($"{name} is outside the window: {bounds}, window {window.Bounds.Size}.");
            if (control is TextBlock text &&
                (text.TextLayout.Width > text.Bounds.Width + 1 || text.TextLayout.Height > text.Bounds.Height + 1))
                throw new InvalidOperationException($"{name} text is clipped: text {text.TextLayout.Width}x{text.TextLayout.Height}, bounds {text.Bounds.Size}.");
            foreach (var other in occupied)
            {
                var overlap = bounds.Intersect(other.Bounds);
                if (overlap.Width > 1 && overlap.Height > 1)
                    throw new InvalidOperationException($"{name} overlaps {other.Name}: {overlap}.");
            }
            occupied.Add((name, bounds));
        }
    }

    private static void TrackClick(Fixture f)
    {
        f.Window.MouseDown(f.At(.25), MouseButton.Left);
        Equal(0, f.Service.AppliedMinutes.Count, "press must not commit");
        f.Window.MouseUp(f.At(.25), MouseButton.Left);
        f.WaitForApply();
        var releasedValue = f.Slider.Value;
        f.Window.MouseUp(f.At(.9), MouseButton.Left);
        f.Window.MouseMove(f.At(.8));
        Near(releasedValue, f.Slider.Value, "button-free movement changed the value");
        Equal(1, f.Service.AppliedMinutes.Count, "click committed more than once");
    }

    private static void Click(Fixture f, string name)
    {
        f.Window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        var target = f.Window.FindControl<Control>(name)!;
        var point = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), f.Window)!.Value;
        f.Window.MouseDown(point, MouseButton.Left);
        f.Window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        f.Window.UpdateLayout();
    }

    private static void MaximizeToggle(Fixture f)
    {
        var button = f.Window.FindControl<Button>("MaximizeButton")!;
        Click(f, "MaximizeButton");
        if (f.Window.WindowState != WindowState.Maximized ||
            AutomationProperties.GetName(button) != "还原" || ToolTip.GetTip(button)?.ToString() != "还原")
            throw new InvalidOperationException("maximize did not update the window state and restore label");
        Click(f, "MaximizeButton");
        if (f.Window.WindowState != WindowState.Normal || AutomationProperties.GetName(button) != "最大化")
            throw new InvalidOperationException("restore did not return to normal state and label");
    }

    private static void TitleDoubleClick(Fixture f)
    {
        Click(f, "TitleDragRegion");
        Click(f, "TitleDragRegion");
        if (f.Window.WindowState != WindowState.Maximized)
            throw new InvalidOperationException("double click did not maximize the window");
    }

    private static void FixedSizeMaximize(Fixture f)
    {
        f.Window.CanResize = false;
        if (f.Window.FindControl<Button>("MaximizeButton")!.IsEnabled)
            throw new InvalidOperationException("maximize button remained enabled for a fixed-size window");
        Click(f, "TitleDragRegion");
        Click(f, "TitleDragRegion");
        if (f.Window.WindowState != WindowState.Normal)
            throw new InvalidOperationException("fixed-size window maximized by double click");
    }

    private static void MinimizeWindow(Fixture f)
    {
        Click(f, "MinimizeButton");
        if (f.Window.WindowState != WindowState.Minimized)
            throw new InvalidOperationException("minimize button did not minimize the window");
    }

    private static void CloseWindow(Fixture f)
    {
        var closed = false;
        f.Window.Closed += (_, _) => closed = true;
        Click(f, "CloseButton");
        if (!closed) throw new InvalidOperationException("close button did not close the window");
    }

    private static void AltF4(Fixture f)
    {
        var closed = false;
        f.Window.Closed += (_, _) => closed = true;
        f.Window.KeyPressQwerty(PhysicalKey.F4, RawInputModifiers.Alt);
        if (!closed) throw new InvalidOperationException("Alt F4 did not close the window");
    }

    private static void TrackDrag(Fixture f)
    {
        f.Window.MouseDown(f.At(.2), MouseButton.Left);
        f.Window.MouseMove(f.At(.6), RawInputModifiers.LeftMouseButton);
        Near(1439 * .6, f.Slider.Value, "left drag did not preview the pointer position");
        Equal(0, f.Service.AppliedMinutes.Count, "drag must not commit before release");
        f.Window.MouseUp(f.At(.8), MouseButton.Left);
        f.WaitForApply();
        Near(1439 * .8, f.Slider.Value, "final release position was not used");
        Equal((int)Math.Round(1439 * .8), f.Service.AppliedMinutes.Single(), "wrong minute committed");
        var releasedValue = f.Slider.Value;
        f.Window.MouseMove(f.At(.1));
        Near(releasedValue, f.Slider.Value, "idle movement resumed the drag");
        Equal(1, f.Service.AppliedMinutes.Count, "drag committed more than once");
    }

    private static void ThumbDrag(Fixture f)
    {
        f.Window.MouseDown(f.ThumbPoint, MouseButton.Left);
        f.Window.MouseMove(f.At(.75), RawInputModifiers.LeftMouseButton);
        Equal(0, f.Service.AppliedMinutes.Count, "thumb committed before release");
        f.Window.MouseUp(f.At(.75), MouseButton.Left);
        f.WaitForApply();
        var releasedValue = f.Slider.Value;
        f.Window.MouseMove(f.At(.15));
        Near(releasedValue, f.Slider.Value, "thumb kept following after release");
        Equal(1, f.Service.AppliedMinutes.Count, "thumb committed more than once");
    }

    private static void OutsideRelease(Fixture f)
    {
        f.Window.MouseDown(f.At(.3), MouseButton.Left);
        Near(1439 * .3, f.Slider.Value, "outside-release press did not hit the slider");
        if (f.Pointer?.Captured != f.Slider)
            throw new InvalidOperationException("slider did not capture the outside-release press");
        f.Window.MouseMove(new Point(-20, -20), RawInputModifiers.LeftMouseButton);
        Near(0, f.Slider.Value, "drag before minimum did not clamp");
        f.Window.MouseUp(new Point(f.Window.Bounds.Width + 20, -20), MouseButton.Left);
        f.WaitForApply();
        Near(1439, f.Slider.Value, "release past maximum did not clamp");
        f.Window.MouseMove(f.At(.5));
        Near(1439, f.Slider.Value, "outside release left dragging active");
        Equal(1, f.Service.AppliedMinutes.Count, "outside release committed more than once");
    }

    private static void CaptureLoss(Fixture f)
    {
        f.Window.MouseDown(f.At(.3), MouseButton.Left);
        f.Pointer!.Capture(null);
        if (f.Pointer.Captured is not null)
            throw new InvalidOperationException("pointer capture was not released");
        var cancelledValue = f.Slider.Value;
        // Release away from the slider and start again immediately. No move is
        // sent to the slider that could hide a missing capture-lost handler.
        f.Window.MouseUp(new Point(0, 0), MouseButton.Left);
        PumpFor(TimeSpan.FromMilliseconds(50));
        Near(cancelledValue, f.Slider.Value, "capture loss changed the preview");
        Equal(0, f.Service.AppliedMinutes.Count, "capture loss applied a cancelled selection");
        f.Window.MouseDown(f.At(.4), MouseButton.Left);
        Near(1439 * .4, f.Slider.Value, "capture loss prevented a fresh drag");
        f.Window.MouseUp(f.At(.4), MouseButton.Left);
        f.WaitForApply();
        Equal(1, f.Service.AppliedMinutes.Count, "fresh drag did not apply exactly once");
    }

    private static void CaptureTransfer(Fixture f)
    {
        f.Window.MouseDown(f.At(.3), MouseButton.Left);
        var newOwner = f.Window.FindControl<TextBlock>("GameTimeText")!;
        f.Pointer!.Capture(newOwner);
        if (f.Pointer.Captured != newOwner)
            throw new InvalidOperationException("drag cleanup released another control's capture");
        var value = f.Slider.Value;
        f.Window.MouseMove(f.At(.8), RawInputModifiers.LeftMouseButton);
        f.Window.MouseUp(f.At(.8), MouseButton.Left);
        f.Pointer.Capture(null);
        PumpFor(TimeSpan.FromMilliseconds(50));
        Near(value, f.Slider.Value, "capture transfer did not cancel the selection");
        Equal(0, f.Service.AppliedMinutes.Count, "capture transfer applied a cancelled selection");
    }

    private static void Deactivated(Fixture f)
    {
        f.Window.MouseDown(f.At(.3), MouseButton.Left);
        var value = f.Slider.Value;
        // Simulate the native platform callback; the interface member is hidden
        // from reference assemblies, so read the headless implementation here.
        var platform = f.Window.PlatformImpl!;
        var deactivated = (Action?)platform.GetType().GetProperty("Deactivated")!.GetValue(platform);
        deactivated!.Invoke();
        if (f.Pointer!.Captured == f.Slider)
            throw new InvalidOperationException("window deactivation retained slider capture");
        f.Window.MouseMove(f.At(.8), RawInputModifiers.LeftMouseButton);
        f.Window.MouseUp(f.At(.8), MouseButton.Left);
        PumpFor(TimeSpan.FromMilliseconds(50));
        Near(value, f.Slider.Value, "window deactivation did not cancel dragging");
        Equal(0, f.Service.AppliedMinutes.Count, "deactivation applied a cancelled selection");
    }

    private static void MissingRelease(Fixture f)
    {
        f.Window.MouseDown(f.At(.3), MouseButton.Left);
        var value = f.Slider.Value;
        f.Window.MouseMove(f.At(.8));
        Near(value, f.Slider.Value, "movement without left button changed value");
        f.Window.MouseUp(f.At(.8), MouseButton.Left);
        PumpFor(TimeSpan.FromMilliseconds(50));
        Equal(0, f.Service.AppliedMinutes.Count, "missing-release cancellation committed");
    }

    private static void IgnoreButton(Fixture f, MouseButton button, bool onThumb)
    {
        var value = f.Slider.Value;
        f.Window.MouseDown(onThumb ? f.ThumbPoint : f.At(.2), button);
        f.Window.MouseMove(f.At(.8), button == MouseButton.Right
            ? RawInputModifiers.RightMouseButton : RawInputModifiers.MiddleMouseButton);
        f.Window.MouseUp(f.At(.8), button);
        f.Window.MouseMove(f.At(.1));
        PumpFor(TimeSpan.FromMilliseconds(50));
        Near(value, f.Slider.Value, "non-left button changed the selection");
        Equal(0, f.Service.AppliedMinutes.Count, "non-left button applied a selection");
    }

    private static void Near(double expected, double actual, string message)
    {
        if (Math.Abs(expected - actual) > .01)
            throw new InvalidOperationException($"{message}: expected {expected:F2}, got {actual:F2}");
    }

    private static void Equal(int expected, int actual, string message)
    {
        if (actual != expected)
            throw new InvalidOperationException($"{message}: expected {expected}, got {actual}");
    }

    internal static void WaitFor(Func<bool> condition, string message)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException(message);
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(1);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static void PumpFor(TimeSpan duration)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < duration)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(1);
        }
    }

    private sealed class Fixture : IDisposable
    {
        public MainWindow Window { get; }
        public UpdateHttpStub UpdateHandler { get; } = new();
        private readonly HttpClient _updateClient;
        private readonly string _updateDirectory = Path.Combine(Path.GetTempPath(), "ets2-ui-updates-" + Guid.NewGuid().ToString("N"));
        public Slider Slider { get; }
        public GameTimeService Service { get; }
        public IPointer? Pointer { get; private set; }

        public Fixture(bool useMinimumSize = false)
        {
            _updateClient = new HttpClient(UpdateHandler);
            Window = new MainWindow([], new UpdateService(_updateClient, _updateDirectory, new Version(1, 1, 3)));
            Service = GameTimeService.Latest;
            if (useMinimumSize)
            {
                Window.Width = Window.MinWidth;
                Window.Height = Window.MinHeight;
            }
            Slider = Window.FindControl<Slider>("TimeSlider")!;
            Window.AddHandler(InputElement.PointerPressedEvent, (_, e) => Pointer = e.Pointer,
                RoutingStrategies.Tunnel, handledEventsToo: true);
            Window.Show();
            var timer = (DispatcherTimer)typeof(MainWindow)
                .GetField("_timer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(Window)!;
            WaitFor(() => timer.IsEnabled,
                "stub game state was not loaded");
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            if (Slider.Bounds.Width <= 0 || Slider.Bounds.Height <= 0)
                throw new InvalidOperationException("slider layout was not completed");
        }

        public Point At(double ratio) => Slider.TranslatePoint(
            new Point(Slider.Bounds.Width * ratio, Slider.Bounds.Height / 2), Window)!.Value;

        public Point ThumbPoint
        {
            get
            {
                var thumb = Slider.GetVisualDescendants().OfType<Thumb>().Single();
                return thumb.TranslatePoint(new Point(thumb.Bounds.Width / 2, thumb.Bounds.Height / 2), Window)!.Value;
            }
        }

        public void WaitForApply() => WaitFor(() => Service.AppliedMinutes.Count > 0, "release never applied selection");

        public void Dispose()
        {
            Pointer?.Capture(null);
            Window.Close();
            Dispatcher.UIThread.RunJobs();
            _updateClient.Dispose();
            if (Directory.Exists(_updateDirectory)) Directory.Delete(_updateDirectory, true);
        }
    }
}
