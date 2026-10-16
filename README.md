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

## Default key bindings (v0.3)

Workspaces are global and each is bound to the display it was created on;
new workspaces are created on the display that has focus. Each display shows
the workspace that is active on it. Windows dragged to another display join
the workspace that is active there after the next rescan (Win+Y or automatic).

| Keys | Action |
|---|---|
| Win + 1 .. Win + 9 | Switch to workspace 1-9 on the display it is bound to; focus follows there, and no other display changes |
| Win + Shift + 1 .. 9 | Move focused window to workspace 1-9 (visible when that workspace is active on its display) |
| Win + Left / Right | Focus previous / next window |
| Win + Shift + Left / Right | Move focused window within its split |
| Win + Enter | Toggle horizontal / vertical split orientation |
| Win + Q | Close focused window |
| Win + X | Move the active workspace to the next monitor |
| Win + Y | Rescan windows and re-apply layout |

While the app runs, it owns the Win key completely: the Start menu never
opens on a Win press, and every key pressed while Win is held either
triggers one of the app's actions or is consumed — shell shortcuts such as
Win+L, Win+D, Win+E and Win+number taskbar activation are disabled
(Ctrl+Esc still opens the Start menu).

Managed windows are locked to their tiled position: moving a window (or
maximizing it) snaps back to its rectangle, and resizing is only allowed
along the split axis — the width in horizontal splits, the height in vertical
splits. A permitted resize is kept and the sibling windows in the split adapt
proportionally; everything else reverts. Use Win+Shift+Left/Right to move
windows within the tiling instead.

Notifications and transient surfaces are never tiled: tool windows,
no-activate windows (toasts, tray pop-ups), menus, tooltips and pop-ups
without a caption.

## Display changes

The app reacts to display configuration changes (a monitor being connected,
disconnected or reconfigured) immediately and re-tiles after a short debounce:

- workspaces bound to a disconnected display move to the primary display;
  the workspace that was active on it becomes active there, so its windows
  stay visible and reachable;
- a reconnecting display starts on a fresh workspace;
- work areas are refreshed on resolution changes, so layouts follow the new
  geometry.

## State

Nothing is persisted between runs. Every start begins fresh: workspace 1 is
bound to the first display, workspace 2 to the second, and so on; new
windows join the workspace that is active on the display they open on.
Hidden windows never linger: on exit every managed window is shown again,
and a window hidden by its own app is unmanaged until it is shown again.

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

- Switching to a workspace never renumbers anything: workspaces keep their
  numbers when they move between displays.
- Elevated windows cannot be tiled by a non-elevated process.
- Planned: keyboard resizing (i3-style grow/shrink hotkeys), floating windows,
  a status bar, and a configuration file for gaps and key bindings.
