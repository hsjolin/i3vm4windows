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

## Default key bindings (v0.1)

| Keys | Action |
|---|---|
| Win + Left / Right | Focus previous / next window |
| Win + Shift + Left / Right | Move focused window within its split |
| Win + Enter | Toggle horizontal / vertical split orientation |
| Win + Q | Close focused window |
| Win + Y | Rescan windows and re-apply layout |

Note: registering Win-based hotkeys overrides some Windows shell shortcuts
(for example Win+Left/Right snapping).

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

- v0.1 tiles windows on the primary monitor's work area only.
- Elevated windows cannot be tiled by a non-elevated process.
- Planned: multiple workspaces per monitor, multi-monitor support,
  manual resizing (i3-style grow/shrink), floating windows, a status bar,
  and a configuration file for gaps and key bindings.
