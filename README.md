# i3vm4windows

A tiling window manager for Windows, inspired by [i3wm](https://i3wm.org/).
Written in C# / .NET 10 with a WPF host process and Win32 interop.

## Design

The project is split in two layers:

| Project | Purpose |
|---|---|
| `src/i3vm4windows.Core` | Pure layout engine: an i3-style container tree (workspace -> split containers -> window leaves), focus/move/orientation operations, and rectangle computation. No Windows dependencies; fully unit tested. |
| `src/i3vm4windows.Config` | i3 configuration parser: comments, line continuations, `set` variables and `bindsym` lines mapped to the app's tiling actions. No Windows dependencies; fully unit tested. |
| `src/i3vm4windows.App` | WPF host process. Discovers top-level windows via `EnumWindows`, tracks show/hide/destroy through `SetWinEventHook`, applies the computed layout with `SetWindowPos`, and registers global hotkeys via `RegisterHotKey` on a message-only window. Runs from the system tray. |
| `tests/i3vm4windows.Core.Tests` | xUnit tests for the layout engine. |
| `tests/i3vm4windows.Config.Tests` | xUnit tests for the configuration parser. |

## Configuration

Key bindings are configured with a regular i3 configuration file:

- Location: `%USERPROFILE%\.config\i3\config` (the Windows counterpart of
  `~/.config/i3/config`), or the path given as the first command-line
  argument.
- When the file is missing or yields no usable bindings, the built-in
  default configuration below applies.
- Supported: comments, blank lines, backslash line continuations,
  `set $name value` variables (quoted or not) and
  `bindsym <keys> <command>` directives. Later bindings for the same key
  combination replace earlier ones, like i3 does.
- Bindings must include the Win key (`Mod4`): the app owns the Win key and
  dispatches combinations through its keyboard hook.

| Command | Action |
|---|---|
| `workspace <n>` / `workspace <n>: name` | Switch to workspace n |
| `workspace back_and_forth` | Switch to the workspace that was active before the current one on this display |
| `move container to workspace <n>` | Move focused window to workspace n |
| `focus left` / `focus right` | Focus previous / next window |
| `move left` / `move right` | Move focused window within its split |
| `split toggle`, `layout toggle split` | Toggle split orientation |
| `layout tabbed` | Present the focused window's split as tabs: one window shows at a time, focus left / right switches between them |
| `layout default` | Return the split to normal tiling |
| `kill` | Close focused window |
| `reload` | Rescan windows and re-apply layout |
| `move workspace to output next` | Move the active workspace to the next monitor |
| `exec [--no-startup-id] <program> [args]` | Start a program, e.g. a rofi-style launcher: `bindsym $mod+Space exec "C:\path\launcher.exe"` — a quoted path keeps its spaces |
| `exec [--no-startup-id] <program> [args]` as its own directive | i3's autostart: start the program when the app starts with this configuration (a reload does not run them again) |
| `mode "<name>"` | Enter a binding mode (or return to the default one with `mode "default"`) |
| `resize grow\|shrink width\|height <px> ...` | Resize the focused window along the split axis; the sibling windows adapt |

Binding modes, like i3's: `mode "name" { ... }` collects its own bindsym
lines — they may omit the Win key — and `mode "name"` enters the mode.
While a mode is active, only its bindings work and unmatched keys still
reach the focused window. The classic resize mode:

```i3
mode "resize" {
  bindsym Left resize shrink width 10 px or 10 ppt
  bindsym Right resize grow width 10 px or 10 ppt
  bindsym Up resize shrink height 10 px or 10 ppt
  bindsym Down resize grow height 10 px or 10 ppt
  bindsym Return mode "default"
  bindsym Escape mode "default"
}
bindsym $mod+r mode "resize"
```

Exec paths expand Windows environment variables (`%USERPROFILE%`) and a
leading `~` to the user profile, so `%USERPROFILE%\copyq\copyq.exe` and
`~\copyq\copyq.exe` both work.

Workspace names must be numeric (plain `3` or `3: web`). Everything the app
cannot perform — non-numeric workspace names, `bindsym --release`,
`bindcode` — is ignored with a warning written to the debug output; other
i3 directives (`font`, `bar`, `gaps`, ...) are ignored
silently.

Window rules: `for_window [class="..." title="..."] floating enable` leaves
matching windows entirely alone — they are never tiled, hidden or moved
(the app has no floating layout). Class and title are case-insensitive
regular expressions, like i3's criteria. Example — Vivaldi's profile
picker, which Chromium-styled windows would otherwise have tiled:

```i3
for_window [class="Chrome_WidgetWin_1" title="^Vivaldi$"] floating enable
```

A move rule sends matching windows to a fixed workspace when they appear,
hidden until that workspace is shown — e.g. every Vivaldi window joins
workspace 3:

```i3
for_window [title="Vivaldi$"] move container to workspace 3
```

Rules apply when a window appears (or after `Win+Y` rescan); windows that
are already managed keep their workspace until moved by hand.

The focused window toggles between tiling and floating with
`floating toggle` (default `Win+Shift+Space`): a tiled window is released
to move and resize freely — visibly inset from the tile it leaves, and
staying floating across rescans and workspace switches — and a floating
window joins the tiling of the display it is on, even when a `for_window`
floating rule excluded it: the explicit toggle wins.

Quake-style toggling (a terminal that shows and hides from any
workspace) binds `[class="..." title="..."] scratchpad show`: a hidden
window shows on the workspace that is active, one visible on another
workspace follows the user instead of hiding, and one already on the
active workspace hides and takes focus back — the window's own
visibility is the state, so the toggle survives restarts:

```i3
bindsym $mod+Tab [class="CASCADIA_HOSTING_WINDOW_CLASS"] scratchpad show
```

Tabs: `layout tabbed` (default `Win+W`) presents the windows of the
focused window's split as tabs — the focused window covers the whole
area and the others hide. Focus left / right (`Win+Left` / `Win+Right`)
switches between them, `layout default` (`Win+E`) returns the split to
normal tiling. A window restored from the taskbar becomes the active tab.

New windows join the workspace that is active on the display you are
working on, even when the program opens its window on another display.

Windows that refuse to shrink below a program-imposed minimum size (most
Chromium-based programs insist on about 500 pixels) keep that size; the
sibling windows share the remaining space, so nothing overlaps.

## Default key bindings (v0.4)

Workspaces are global and each is bound to the display it was created on;
new workspaces are created on the display that has focus. Each display shows
the workspace that is active on it. Windows dragged to another display join
the workspace that is active there after the next rescan (Win+Y or automatic).
The bindings below are the built-in default configuration; a user
configuration file replaces them.

| Keys | Action |
|---|---|
| Win + 1 .. Win + 9 | Switch to workspace 1-9 on the display it is bound to; focus and mouse pointer follow there, and no other display changes |
| Win + Shift + 1 .. 9 | Move focused window to workspace 1-9 (visible when that workspace is active on its display) |
| Win + Left / Right | Focus previous / next window |
| Win + Shift + Left / Right | Move focused window within its split |
| Win + Enter | Toggle horizontal / vertical split orientation |
| Win + W | Present the split as tabs |
| Win + E | Return the split to normal tiling |
| Win + Q | Close focused window |
| Win + X | Move the active workspace to the next monitor |
| Win + Y | Rescan windows and re-apply layout |
| Win + Left drag | Move the window under the pointer; a tiled window is released from the tiling (tile it back with `Win+Shift+Space`) |
| Win + Right drag | Resize the window by the corner nearest the click |

While the app runs, it owns the Win key completely: the Start menu never
opens on a Win press, and every key pressed while Win is held either
triggers one of the app's actions or is consumed — shell shortcuts such as
Win+L, Win+D, Win+E and Win+number taskbar activation are disabled
(Ctrl+Esc still opens the Start menu). This also means no other program
can see the Win key, so mouse utilities like AltSnap must use another
modifier (its default Alt works fine).

Each display carries a persistent workspace indicator in the bottom-left
corner of its work area: the workspaces bound to that display with the
active one highlighted, dimmed while at rest and lighting up briefly
whenever the active workspace changes (Win+1..9, Win+X). The indicator
never takes focus and clicks fall through to the tiling below it.

Managed windows are locked to their tiled position: moving a window (or
maximizing it) snaps back to its rectangle, and resizing is only allowed
along the split axis — the width in horizontal splits, the height in vertical
splits. A permitted resize is kept and the sibling windows in the split adapt
proportionally; everything else reverts. Use Win+Shift+Left/Right to move
windows within the tiling instead.

Notifications and transient surfaces are never tiled: dialog boxes,
message boxes and every other owned window (they stay above their owner,
like i3 floats the dialog window type), tool windows, no-activate
windows (toasts, tray pop-ups), menus, tooltips and pop-ups without a
caption.

## Display changes

The app reacts to display configuration changes (a monitor being connected,
disconnected or reconfigured) immediately and re-tiles after a short debounce:

- workspaces bound to a disconnected display move to the primary display;
  the workspace that was active on it becomes active there, so its windows
  stay visible and reachable;
- a reconnecting display starts on a fresh workspace;
- work areas are refreshed on resolution changes, so layouts follow the new
  geometry.

## Logging

The app writes a plain-text log to `%LOCALAPPDATA%\i3vm4windows\log.txt`:
configuration loading (with every parse warning), hotkey dispatches, the
win_key passthrough decision on every Win press, display changes and
refresh failures — each line time-stamped. The tray menu's "Open log"
opens it in your default text editor.

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
  a status bar, and configuration of gaps and layout options.
