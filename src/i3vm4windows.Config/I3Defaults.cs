namespace i3vm4windows.Config;

/// <summary>
/// The built-in bindings used when no configuration file is found. The text
/// is a regular i3 configuration; the same subset of commands is available
/// to user configuration files.
/// </summary>
public static class I3Defaults
{
    public const string Text = """
        set $mod Mod4

        # Focus and move within the active workspace.
        bindsym $mod+Left focus left
        bindsym $mod+Right focus right
        bindsym $mod+Shift+Left move left
        bindsym $mod+Shift+Right move right

        # Splitting, closing, rescanning and monitor moves.
        bindsym $mod+Enter split toggle
        bindsym $mod+q kill
        bindsym $mod+y reload
        bindsym $mod+x move workspace to output next

        # Switch to workspace 1..9.
        bindsym $mod+1 workspace 1
        bindsym $mod+2 workspace 2
        bindsym $mod+3 workspace 3
        bindsym $mod+4 workspace 4
        bindsym $mod+5 workspace 5
        bindsym $mod+6 workspace 6
        bindsym $mod+7 workspace 7
        bindsym $mod+8 workspace 8
        bindsym $mod+9 workspace 9

        # Move focused window to workspace 1..9.
        bindsym $mod+Shift+1 move container to workspace 1
        bindsym $mod+Shift+2 move container to workspace 2
        bindsym $mod+Shift+3 move container to workspace 3
        bindsym $mod+Shift+4 move container to workspace 4
        bindsym $mod+Shift+5 move container to workspace 5
        bindsym $mod+Shift+6 move container to workspace 6
        bindsym $mod+Shift+7 move container to workspace 7
        bindsym $mod+Shift+8 move container to workspace 8
        bindsym $mod+Shift+9 move container to workspace 9
        """;
}
