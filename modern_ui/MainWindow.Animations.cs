using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using System.Runtime.InteropServices;

namespace ETS2DayNightModern;

public partial class MainWindow
{
    private CancellationTokenSource? _entranceAnimation;
    private CancellationTokenSource? _environmentAnimation;
    private bool _animationsEnabled;
    private bool _animationWindowOpened;

    private void InitializeAnimations()
    {
        UpdateAnimationPreference();
        Activated += (_, _) => UpdateAnimationPreference();
        Opened += async (_, _) =>
        {
            _animationWindowOpened = true;
            if (!_animationsEnabled) return;
            // Fade the content without moving its hit targets: a drag may start
            // immediately after launch, before the entrance has completed.
            var content = TimeSlider.GetLogicalAncestors().OfType<ScrollViewer>()
                .FirstOrDefault()?.Content as Control;
            if (content is null) return;
            _entranceAnimation = new CancellationTokenSource();
            await FadeInAsync(content, .25, 260, _entranceAnimation.Token);
        };
        EnvironmentText.PropertyChanged += async (_, args) =>
        {
            if (args.Property != TextBlock.TextProperty || !_animationWindowOpened || !_animationsEnabled)
                return;
            _environmentAnimation?.Cancel();
            _environmentAnimation?.Dispose();
            _environmentAnimation = new CancellationTokenSource();
            await FadeInAsync(EnvironmentText, .55, 180, _environmentAnimation.Token);
        };
        Closed += (_, _) =>
        {
            _entranceAnimation?.Cancel();
            _entranceAnimation?.Dispose();
            _environmentAnimation?.Cancel();
            _environmentAnimation?.Dispose();
        };
    }

    private void UpdateAnimationPreference()
    {
        _animationsEnabled = !OperatingSystem.IsWindows() ||
            !SystemParametersInfo(0x1042, 0, out var enabled, 0) || enabled;
        Classes.Set("motion", _animationsEnabled);
        if (!_animationsEnabled)
        {
            _entranceAnimation?.Cancel();
            _environmentAnimation?.Cancel();
        }
    }

    private static async Task FadeInAsync(Control target, double from, int milliseconds,
        CancellationToken cancellationToken)
    {
        var animation = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(milliseconds),
            Easing = new CubicEaseOut(),
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(OpacityProperty, from) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(OpacityProperty, 1d) } }
            }
        };
        try
        {
            await animation.RunAsync(target, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Cancelling removes the animation value and restores full opacity.
        }
    }

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint parameter,
        [MarshalAs(UnmanagedType.Bool)] out bool value, uint flags);
}
