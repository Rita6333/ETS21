# Slider pointer regression

This runner compiles the real `modern_ui/MainWindow.axaml` and code behind with
Avalonia.Headless 11.2.5 and the Fluent slider template. Mouse events travel through
the real input system, including track buttons, thumb, capture and routed events.
The game service and elevation helper are substitutes: no game process is read or
written, and no desktop window is created.

The 21 scenarios cover slider capture/release behavior, the update overlay and the custom title bar:
maximize/restore state and labels, title-bar double click, fixed-size behavior,
minimize, close and Alt+F4. Native window movement, resize borders and the Windows
system menu use the platform API and are not simulated by the headless runner.
Update checks use an in-memory HTTP substitute and a temporary cache; no release
server is contacted and no real installer is launched.

Run from the workspace root:

```powershell
.\_deps\dotnet8\sdk\dotnet.exe restore tests/SliderInteraction/SliderInteraction.csproj --source https://api.nuget.org/v3/index.json
.\_deps\dotnet8\sdk\dotnet.exe run --project tests/SliderInteraction/SliderInteraction.csproj --no-restore
```

An optional command line substring selects test names. The MSBuild property
`MainWindowCodeBehind` can point to a saved earlier code behind to check that the
same scenarios reproduce a regression.

The runner loads the same dark theme and fonts as the shipped window.
To render its default size, minimum size, locked and waiting states offscreen:

```powershell
.\_deps\dotnet8\sdk\dotnet.exe run --project tests/SliderInteraction/SliderInteraction.csproj --no-restore -- --render _case/ui_previews
```

Rendering uses Skia with a headless window, validates that the key controls fit
inside the window without text clipping or overlap, and saves PNGs without
opening a desktop window or reading the game. The minimum preview uses the
window's declared minimum width and height.
