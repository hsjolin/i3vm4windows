# i3vm4windows

A tiling window manager for Windows, inspired by [i3wm](https://i3wm.org/).
Written in C# / .NET 10 with a WPF host process and Win32 interop.

## Design

The project is split in two layers:

| Project | Purpose |
|---|---|
| `src/i3vm4windows.Core` | Pure layout engine: an i3-style container tree (workspace -> split containers -> window leaves), focus/move/orientation operations, and rectangle computation. No Windows dependencies; fully unit tested. |
| `src/i3vm4windows.App` | WPF host process. Discovers top-level windows via `EnumWindows`, tracks show/hide/destroy through `SetWinEventHook`, applies the computed layout with `SetWindowPos`, and registers global hotkeys via `RegisterHotKey` on a message-only window. Runs from the system tray. |
| `tests/i3vm4windows.Core.Tests` | xUnit tests for the layout engine. |

## Default key bindings (v0.2)

Each monitor has its own independent set of workspaces. Workspace hotkeys act
on the monitor that shows the foreground window (falling back to the primary
monitor). Windows that are dragged to another monitor, or whose monitor is
disconnected, migrate to the active workspace of the monitor they end up on
after the next rescan (Win+Y or automatic).

| Keys | Action |
|---|---|
| Win + 1 .. Win + 9 | Switch to workspace 1-9 |
| Win + Shift + 1 .. 9 | Move focused window to workspace 1-9 |
| Win + Left / Right | Focus previous / next window |
| Win + Shift + Left / Right | Move focused window within its split |
| Win + Enter | Toggle horizontal / vertical split orientation |
| Win + Q | Close focused window |
| Win + X | Move the active workspace to the next monitor |
| Win + Y | Rescan windows and re-apply layout |

Note: registering Win-based hotkeys overrides some Windows shell shortcuts
(for example Win+Left/Right snapping and Win+number taskbar activation).

## Display changes

The app reacts to display configuration changes (a monitor being connected,
disconnected or reconfigured) immediately and re-tiles after a short debounce:

- windows from a disconnected monitor join the active workspace of the
  monitor they are moved to, and windows that were hidden on an inactive
  workspace become visible again;
- a reconnecting monitor gets its remembered active workspace back, while
  newly connected monitors start on workspace 1;
- work areas are refreshed on resolution changes, so layouts follow the new
  geometry.

## State persistence

Between runs the app remembers its state in `%APPDATA%\i3vm4windows\state.json`:

- the active workspace of each monitor, keyed by display device name, and
- which workspace each managed window was last tiled on.

Windows are identified by their process image path and window title, with a
fallback to the process image alone. A window that reopens with a matching
identity is placed back on its remembered workspace (hidden if that workspace
is not active); unknown windows join the active workspace as usual. Up to 300
placements are kept. State is saved whenever it changes and on exit.

Hidden windows never linger: on exit every managed window is shown again, and
windows that a crashed instance left hidden are re-adopted (shown on their
remembered workspace) on the next start.

## Building

Requires the .NET 10 SDK on Windows:

```powershell
dotnet build i3vm4windows.sln
dotnet test i3vm4windows.sln
dotnet run --project src/i3vm4windows.App
```

From WSL you can use the Windows SDK directly:

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" build /mnt/c/workspace/i3vm4windows/i3vm4windows.sln
```

## Limitations / roadmap

- Workspaces are per monitor: switching to a workspace never moves windows
  between screens (a future release may adopt i3's output-bound workspaces).
- Elevated windows cannot be tiled by a non-elevated process.
- Planned: manual resizing (i3-style grow/shrink), floating windows, a status
  bar, and a configuration file for gaps and key bindings.
