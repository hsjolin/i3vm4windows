using i3vm4windows.Config;
using i3vm4windows.Core;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace i3vm4windows.Config.Tests;

public class I3ConfigParserTests
{
    [Fact]
    public void Bindsym_with_variable_substitutes_the_variable()
    {
        var config = I3ConfigParser.Parse("""
            set $mod Mod4
            bindsym $mod+1 workspace 1
            """);

        var binding = Assert.Single(config.Bindings);
        Assert.Equal(Modifiers.Win, binding.Modifiers);
        Assert.Equal("1", binding.Key);
        Assert.Equal(ConfigAction.SwitchWorkspace, binding.Action);
        Assert.Equal(1, binding.Parameter);
        Assert.Empty(config.Warnings);
    }

    [Fact]
    public void Longest_variable_wins_when_names_share_a_prefix()
    {
        var config = I3ConfigParser.Parse("""
            set $ws1 1:one
            set $ws10 10:ten
            bindsym Mod4+Shift+0 move container to workspace $ws10
            """);

        var binding = Assert.Single(config.Bindings);
        Assert.Equal(ConfigAction.MoveToWorkspace, binding.Action);
        Assert.Equal(10, binding.Parameter);
    }

    [Fact]
    public void Quoted_variable_values_lose_their_quotes()
    {
        var config = I3ConfigParser.Parse("""
            set $ws1 "1: web"
            bindsym Mod4+1 workspace $ws1
            """);

        var binding = Assert.Single(config.Bindings);
        Assert.Equal(ConfigAction.SwitchWorkspace, binding.Action);
        Assert.Equal(1, binding.Parameter);
    }

    [Fact]
    public void Modifiers_map_to_i3_names()
    {
        var config = I3ConfigParser.Parse("""
            bindsym Mod4+Shift+Mod1+Control+x kill
            """);

        var binding = Assert.Single(config.Bindings);
        Assert.Equal(
            Modifiers.Win | Modifiers.Shift | Modifiers.Alt | Modifiers.Control,
            binding.Modifiers);
        Assert.Equal("X", binding.Key);
    }

    [Fact]
    public void Key_names_normalize_to_canonical_forms()
    {
        var config = I3ConfigParser.Parse("""
            set $mod Mod4
            bindsym $mod+return kill
            bindsym $mod+q kill
            """);

        Assert.Equal(new[] { "Enter", "Q" }, config.Bindings.Select(b => b.Key));
    }

    [Theory]
    [InlineData("focus left", ConfigAction.FocusPrevious)]
    [InlineData("focus right", ConfigAction.FocusNext)]
    [InlineData("move left", ConfigAction.MovePrevious)]
    [InlineData("move right", ConfigAction.MoveNext)]
    [InlineData("kill", ConfigAction.CloseFocused)]
    [InlineData("split toggle", ConfigAction.ToggleOrientation)]
    [InlineData("layout toggle split", ConfigAction.ToggleOrientation)]
    [InlineData("floating toggle", ConfigAction.FloatingToggle)]
    [InlineData("reload", ConfigAction.Refresh)]
    [InlineData("move workspace to output next", ConfigAction.MoveWorkspaceToNextMonitor)]
    public void Known_commands_map_to_actions(string command, ConfigAction expected)
    {
        var config = I3ConfigParser.Parse($"bindsym Mod4+x {command}");

        var binding = Assert.Single(config.Bindings);
        Assert.Equal(expected, binding.Action);
        Assert.Empty(config.Warnings);
    }

    [Fact]
    public void Numbered_workspace_names_use_their_number()
    {
        var config = I3ConfigParser.Parse("""
            bindsym Mod4+1 workspace 3: web
            bindsym Mod4+Shift+1 move container to workspace 2: mail
            """);

        Assert.Equal(ConfigAction.SwitchWorkspace, config.Bindings[0].Action);
        Assert.Equal(3, config.Bindings[0].Parameter);
        Assert.Equal(ConfigAction.MoveToWorkspace, config.Bindings[1].Action);
        Assert.Equal(2, config.Bindings[1].Parameter);
    }

    [Fact]
    public void Later_binding_replaces_earlier_one_for_the_same_combination()
    {
        var config = I3ConfigParser.Parse("""
            bindsym Mod4+x kill
            bindsym Mod4+x reload
            """);

        var binding = Assert.Single(config.Bindings);
        Assert.Equal(ConfigAction.Refresh, binding.Action);
    }

    [Fact]
    public void Comments_and_blank_lines_are_ignored()
    {
        var config = I3ConfigParser.Parse("""
            # font pango:monospace 8

            # A comment before a binding.
            bindsym Mod4+x kill # and one after it.
            """);

        var binding = Assert.Single(config.Bindings);
        Assert.Equal(ConfigAction.CloseFocused, binding.Action);
        Assert.Empty(config.Warnings);
    }

    [Fact]
    public void Backslash_continuations_join_lines()
    {
        var config = I3ConfigParser.Parse("""
            bindsym Mod4+x \
                kill
            """);

        var binding = Assert.Single(config.Bindings);
        Assert.Equal(ConfigAction.CloseFocused, binding.Action);
    }

    [Fact]
    public void Unknown_directives_warn()
    {
        var config = I3ConfigParser.Parse("""
            font pango:monospace 8
            gaps inner 8
            bar {
                status_command i3status
            }
            """);

        Assert.Empty(config.Bindings);
        Assert.Equal(3, config.Warnings.Count);
        Assert.Contains(config.Warnings, warning =>
            warning.StartsWith("Unknown statement:"));
    }

    [Fact]
    public void Unsupported_commands_and_keys_produce_warnings()
    {
        var config = I3ConfigParser.Parse("""
            bindsym Mod4+F1 nop
            bindsym Mod4+b workspace browsing
            bindsym Mod4+XF86AudioRaiseVolume nop
            bindsym Ctrl+x kill
            """);

        Assert.Empty(config.Bindings);
        Assert.Equal(4, config.Warnings.Count);
        Assert.Contains(config.Warnings, w => w.Contains("Unsupported command"));
        Assert.Contains(config.Warnings, w => w.Contains("Unsupported key combination"));
        Assert.Contains(config.Warnings, w => w.Contains("Win key"));
    }

    [Fact]
    public void Exec_launches_a_program_with_its_arguments()
    {
        var config = I3ConfigParser.Parse(
            """
            bindsym Mod4+Space exec Flow.Launcher.exe
            bindsym Mod4+D exec --no-startup-id "C:\Program Files\My App\Roofi.exe" --flag on
            """);

        Assert.Equal(2, config.Bindings.Count);
        Assert.All(config.Bindings, b => Assert.Equal(ConfigAction.Launch, b.Action));

        Assert.Equal(Modifiers.Win, config.Bindings[0].Modifiers);
        Assert.Equal("Space", config.Bindings[0].Key);
        Assert.Equal("Flow.Launcher.exe", config.Bindings[0].Command);

        // Quoted path with spaces keeps its quotes and case; the i3 marker
        // is dropped and the arguments survive.
        Assert.Equal(
            "\"C:\\Program Files\\My App\\Roofi.exe\" --flag on",
            config.Bindings[1].Command);
    }

    [Fact]
    public void Exec_without_a_program_warns()
    {
        var config = I3ConfigParser.Parse("bindsym Mod4+Space exec");

        Assert.Empty(config.Bindings);
        Assert.Single(config.Warnings);
    }

    [Fact]
    public void Scratchpad_show_toggles_the_windows_its_criteria_target()
    {
        var config = I3ConfigParser.Parse(
            """
            bindsym Mod4+Tab [class="CASCADIA_HOSTING_WINDOW_CLASS"] scratchpad show
            bindsym Mod4+Z [class="CASCADIA_HOSTING_WINDOW_CLASS" title="Ubuntu"] scratchpad show
            """);

        Assert.Equal(2, config.Bindings.Count);
        Assert.All(config.Bindings, b => Assert.Equal(ConfigAction.ScratchpadShow, b.Action));
        Assert.All(config.Bindings, b => Assert.NotNull(b.Rule));
        Assert.True(config.Bindings[0].Rule!.Matches("CASCADIA_HOSTING_WINDOW_CLASS", "Ubuntu"));
        Assert.False(config.Bindings[0].Rule!.Matches("Chrome_WidgetWin_1", "Ubuntu"));
        Assert.True(config.Bindings[1].Rule!.Matches("CASCADIA_HOSTING_WINDOW_CLASS", "Ubuntu"));
        Assert.False(config.Bindings[1].Rule!.Matches("CASCADIA_HOSTING_WINDOW_CLASS", "something else"));
        Assert.Empty(config.Warnings);
    }

    [Fact]
    public void Scratchpad_show_without_criteria_warns()
    {
        var config = I3ConfigParser.Parse("bindsym Mod4+Tab scratchpad show");

        Assert.Empty(config.Bindings);
        Assert.Single(config.Warnings);
    }

    [Fact]
    public void Release_bindings_and_bindcode_produce_warnings()
    {
        var config = I3ConfigParser.Parse("""
            bindsym --release Mod4+x exec notify-send done
            bindcode 24 kill
            """);

        Assert.Empty(config.Bindings);
        Assert.Equal(2, config.Warnings.Count);
    }

    [Fact]
    public void The_default_configuration_parses_into_the_expected_bindings()
    {
        var config = I3ConfigParser.Parse(I3Defaults.Text);

        Assert.Empty(config.Warnings);
        Assert.Equal(29, config.Bindings.Count);

        var switchWorkspace = config.Bindings.Where(b => b.Action == ConfigAction.SwitchWorkspace);
        Assert.Equal(9, switchWorkspace.Count());

        var moveToWorkspace = config.Bindings.Where(b => b.Action == ConfigAction.MoveToWorkspace);
        Assert.Equal(9, moveToWorkspace.Count());
        Assert.Contains(config.Bindings, b =>
            b.Modifiers == (Modifiers.Win | Modifiers.Shift) &&
            b.Key == "1" &&
            b.Action == ConfigAction.MoveToWorkspace &&
            b.Parameter == 1);

        Assert.Contains(config.Bindings, b =>
            b.Modifiers == Modifiers.Win && b.Key == "Enter");
        Assert.Contains(config.Bindings, b =>
            b.Modifiers == Modifiers.Win && b.Key == "Q");
        Assert.Contains(config.Bindings, b =>
            b.Modifiers == Modifiers.Win && b.Key == "W" &&
            b.Action == ConfigAction.SetTabbed);
        Assert.Contains(config.Bindings, b =>
            b.Modifiers == Modifiers.Win && b.Key == "E" &&
            b.Action == ConfigAction.SetSplit);
    }

    [Fact]
    public void An_empty_configuration_has_no_bindings()
    {
        var config = I3ConfigParser.Parse("");

        Assert.Empty(config.Bindings);
        Assert.Empty(config.Warnings);
    }

    [Fact]
    public void For_window_floating_enable_creates_a_rule()
    {
        var config = I3ConfigParser.Parse(
            """
            for_window [class="Chrome_WidgetWin_1" title="^Vivaldi$"] floating enable
            """);

        var rule = Assert.Single(config.Rules);
        Assert.True(rule.Matches("Chrome_WidgetWin_1", "Vivaldi"));
        Assert.False(rule.Matches("Chrome_WidgetWin_1", "Some page - Vivaldi"));
        Assert.False(rule.Matches("MozillaWindowClass", "Vivaldi"));
        Assert.Empty(config.Warnings);
    }

    [Fact]
    public void For_window_with_class_only_matches_any_title()
    {
        var config = I3ConfigParser.Parse(
            """
            for_window [class="^Mozilla"] floating enable
            """);

        var rule = Assert.Single(config.Rules);
        Assert.True(rule.Matches("MozillaWindowClass", "anything"));
        Assert.False(rule.Matches("Chrome_WidgetWin_1", "anything"));
    }

    [Fact]
    public void For_window_criteria_may_contain_spaces()
    {
        var config = I3ConfigParser.Parse(
            """
            for_window [class="Chrome_WidgetWin_1" title="^Vivaldi$"] floating enable
            """);

        Assert.Single(config.Rules);
        Assert.True(config.Rules[0].Matches("Chrome_WidgetWin_1", "Vivaldi"));
    }

    [Fact]
    public void For_window_matching_is_case_insensitive()
    {
        var config = I3ConfigParser.Parse(
            """
            for_window [class="chrome_widgetwin_1"] floating enable
            """);

        Assert.True(Assert.Single(config.Rules).Matches("Chrome_WidgetWin_1", "x"));
    }

    [Fact]
    public void For_window_without_criteria_or_with_unknown_commands_warns()
    {
        var config = I3ConfigParser.Parse("""
            for_window floating enable
            for_window [class="x"] kill
            for_window [class="("] floating enable
            """);

        Assert.Empty(config.Rules);
        Assert.Equal(4, config.Warnings.Count);
    }

    [Fact]
    public void For_window_move_container_to_workspace_creates_a_move_rule()
    {
        var config = I3ConfigParser.Parse("""
            for_window [title="Vivaldi$"] move container to workspace 3
            """);

        var rule = Assert.Single(config.Rules);
        Assert.Equal(WindowRuleAction.MoveToWorkspace, rule.Action);
        Assert.Equal(3, rule.Parameter);
        Assert.Null(rule.Class);
        Assert.True(rule.Matches("any", "Something - Vivaldi"));
        Assert.False(rule.Matches("any", "Vivaldi - Something else"));
    }

    [Fact]
    public void For_window_move_to_workspace_accepts_numbered_names()
    {
        var config = I3ConfigParser.Parse("""
            for_window [class="Term"] move to workspace 3: web
            """);

        var rule = Assert.Single(config.Rules);
        Assert.Equal(WindowRuleAction.MoveToWorkspace, rule.Action);
        Assert.Equal(3, rule.Parameter);
    }

    [Fact]
    public void For_window_move_to_unknown_workspace_warns()
    {
        var config = I3ConfigParser.Parse("""
            for_window [class="Term"] move container to workspace web
            """);

        Assert.Empty(config.Rules);
        Assert.Single(config.Warnings);
    }

    [Fact]
    public void Exec_directive_collects_the_startup_command()
    {
        var config = I3ConfigParser.Parse("""
            exec --no-startup-id "%USERPROFILE%\copyq\copyq.exe"
            """);

        var command = Assert.Single(config.Exec);
        Assert.Equal("\"%USERPROFILE%\\copyq\\copyq.exe\"", command);
        Assert.Empty(config.Warnings);
    }

    [Fact]
    public void Exec_directive_keeps_arguments_and_case()
    {
        var config = I3ConfigParser.Parse("""
            exec C:\Tools\MyTool.exe Start Something
            """);

        var command = Assert.Single(config.Exec);
        Assert.Equal("C:\\Tools\\MyTool.exe Start Something", command);
    }

    [Fact]
    public void Exec_directive_without_a_program_warns()
    {
        var config = I3ConfigParser.Parse("""
            exec --no-startup-id
            """);

        Assert.Empty(config.Exec);
        Assert.Single(config.Warnings);
    }

    [Fact]
    public void Exec_commands_in_bindings_do_not_autostart()
    {
        var config = I3ConfigParser.Parse("""
            bindsym Mod4+c exec --no-startup-id "%USERPROFILE%\copyq\copyq.exe" toggle
            """);

        Assert.Empty(config.Exec);
        var binding = Assert.Single(config.Bindings);
        Assert.Equal(ConfigAction.Launch, binding.Action);
    }

    [Fact]
    public void Mode_block_binds_without_the_win_key()
    {
        var config = I3ConfigParser.Parse("""
            mode "resize" {
              bindsym Left resize shrink width 10 px or 10 ppt
              bindsym Right resize grow width 10 px or 10 ppt
              bindsym Return mode "default"
              bindsym Escape mode "default"
            }
            bindsym Mod4+r mode "resize"
            """);

        Assert.Empty(config.Warnings);
        Assert.Equal(5, config.Bindings.Count);

        var enter = config.Bindings.Single(b => b.Key == "R");
        Assert.Equal("default", enter.Mode);
        Assert.Equal(ConfigAction.Mode, enter.Action);
        Assert.Equal("resize", enter.Command);

        var shrink = config.Bindings.Single(b => b.Mode == "resize" && b.Key == "Left");
        Assert.Equal(Modifiers.None, shrink.Modifiers);
        Assert.Equal(ConfigAction.ResizeShrinkWidth, shrink.Action);
        Assert.Equal(10, shrink.Parameter);

        var leave = config.Bindings.Single(b => b.Mode == "resize" && b.Key == "Enter");
        Assert.Equal(ConfigAction.Mode, leave.Action);
        Assert.Equal("default", leave.Command);
    }

    [Fact]
    public void Resize_height_commands_map_to_grow_and_shrink()
    {
        var config = I3ConfigParser.Parse("""
            mode "resize" {
              bindsym Up resize shrink height 10 px or 10 ppt
              bindsym Down resize grow height 10 px or 10 ppt
            }
            """);

        var up = config.Bindings.Single(b => b.Key == "Up");
        Assert.Equal(ConfigAction.ResizeShrinkHeight, up.Action);
        var down = config.Bindings.Single(b => b.Key == "Down");
        Assert.Equal(ConfigAction.ResizeGrowHeight, down.Action);
    }

    [Fact]
    public void Workspace_back_and_forth_maps_to_the_action()
    {
        var config = I3ConfigParser.Parse("""
            bindsym Mod4+Escape workspace back_and_forth
            """);

        var binding = Assert.Single(config.Bindings);
        Assert.Equal(ConfigAction.SwitchBackAndForth, binding.Action);
    }

    [Fact]
    public void Bindings_without_win_warn_outside_mode_blocks()
    {
        var config = I3ConfigParser.Parse("""
            bindsym Left focus left
            """);

        Assert.Empty(config.Bindings);
        Assert.Single(config.Warnings);
    }

    [Fact]
    public void Win_key_directives_are_ignored_with_a_warning()
    {
        var config = I3ConfigParser.Parse("""
            win_key passthrough
            remap_win_key LCtrl
            """);

        Assert.Equal(2, config.Warnings.Count);
    }

    [Fact]
    public void Custom_statements_resolve_by_name_from_the_container()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddSingleton<IConfigStatement>(new LogStatement());
        var config = I3ConfigParser.Parse("bindsym Mod4+l log everything", services);

        var binding = Assert.Single(config.Bindings);
        Assert.Equal("L", binding.Key);
        Assert.Equal(ConfigAction.Launch, binding.Action);
        Assert.Equal("LOG everything", binding.Command);
        Assert.Empty(config.Warnings);

        // The statement owns the behavior its binding runs.
        var log = new List<string>();
        binding.Run?.Invoke(new RecordingRuntime(log), null);
        Assert.Equal(new[] { "LOG everything" }, log);
    }

    /// <summary>A statement added by the caller, registered in the same
    /// container as the built-ins and resolved by its name.</summary>
    private sealed record LogStatement : IConfigStatement
    {
        public string Name => "log";

        public void Execute(ConfigContext context, IReadOnlyList<string> parameters) =>
            context.Intent = new CommandIntent(
                Action: ConfigAction.Launch,
                Command: "LOG " + string.Join(' ', parameters),
                Run: (runtime, _) => runtime.Launch("LOG " + string.Join(' ', parameters)));
    }

    /// <summary>The runtime a test can observe.</summary>
    private sealed class RecordingRuntime(List<string> log) : IConfigRuntime
    {
        public void Launch(string command) => log.Add(command);
        public void FocusRelative(int offset) { }
        public void MoveRelative(int offset) { }
        public void SwitchWorkspace(int index) { }
        public void SwitchBackAndForth() { }
        public void MoveFocusedToWorkspace(int index) { }
        public void MoveActiveWorkspaceToNextMonitor() { }
        public void ToggleOrientation() { }
        public void SetTabbed() { }
        public void SetSplit() { }
        public void CloseFocused() { }
        public void Refresh() { }
        public void ToggleFloating() { }
        public void ToggleScratchpad(WindowRule rule) { }
        public void ResizeFocused(ResizeAxis axis, int delta) { }
        public void SetMode(string name) { }
    }

    [Fact]
    public void For_window_move_scratchpad_creates_a_scratchpad_rule()
    {
        var config = I3ConfigParser.Parse("""
            for_window [title="^Möteskomprimeringsvy"] move scratchpad
            """);

        var rule = Assert.Single(config.Rules);
        Assert.Equal(WindowRuleAction.Scratchpad, rule.Action);
        Assert.True(rule.Matches("any", "Möteskomprimeringsvy - Teams"));
        Assert.Empty(config.Warnings);
    }

    [Fact]
    public void Move_scratchpad_is_not_a_binding_command()
    {
        var config = I3ConfigParser.Parse("""
            bindsym Mod4+m move scratchpad
            """);

        Assert.Empty(config.Bindings);
        Assert.Single(config.Warnings);
    }

    [Fact]
    public void Unknown_statement_warns()
    {
        var config = I3ConfigParser.Parse("frobnicate loudly");

        var warning = Assert.Single(config.Warnings);
        Assert.Contains("Unknown statement", warning);
    }

    [Fact]
    public void Stray_block_closing_warns()
    {
        var config = I3ConfigParser.Parse("}");

        var warning = Assert.Single(config.Warnings);
        Assert.Contains("No block to close", warning);
    }

    [Fact]
    public void Redefined_variables_keep_their_names()
    {
        var config = I3ConfigParser.Parse("""
            set $mod Mod4
            set $mod Mod4
            bindsym $mod+1 workspace 1
            """);

        var binding = Assert.Single(config.Bindings);
        Assert.Equal(Modifiers.Win, binding.Modifiers);
        Assert.Empty(config.Warnings);
    }

    [Fact]
    public void Layout_tabbed_maps_to_SetTabbed()
    {
        var config = I3ConfigParser.Parse("""
            bindsym Mod4+w layout tabbed
            """);

        var binding = Assert.Single(config.Bindings);
        Assert.Equal(ConfigAction.SetTabbed, binding.Action);
        Assert.Empty(config.Warnings);
    }

    [Fact]
    public void Layout_default_maps_to_SetSplit()
    {
        var config = I3ConfigParser.Parse("""
            bindsym Mod4+e layout default
            """);

        var binding = Assert.Single(config.Bindings);
        Assert.Equal(ConfigAction.SetSplit, binding.Action);
        Assert.Empty(config.Warnings);
    }

    [Fact]
    public void Layout_stacking_is_not_supported()
    {
        var config = I3ConfigParser.Parse("""
            bindsym Mod4+s layout stacking
            """);

        Assert.Empty(config.Bindings);
        Assert.Single(config.Warnings);
    }

    [Fact]
    public void Min_window_width_directive_sets_the_minimum()
    {
        var config = I3ConfigParser.Parse("""
            min_window_width 500
            bindsym Mod4+q kill
            """);

        Assert.Equal(500, config.MinWindowWidth);
        Assert.Empty(config.Warnings);
    }

    [Fact]
    public void Workspace_layout_tabbed_sets_the_default_of_new_workspaces()
    {
        var config = I3ConfigParser.Parse("""
            workspace_layout tabbed
            bindsym Mod4+q kill
            """);

        Assert.Equal(LayoutMode.Tabbed, config.DefaultWorkspaceLayout);
        Assert.Empty(config.Warnings);
    }

    [Fact]
    public void Workspace_layout_defaults_to_split_and_rejects_invalid_values()
    {
        var withoutDirective = I3ConfigParser.Parse("""
            bindsym Mod4+q kill
            """);
        Assert.Equal(LayoutMode.Split, withoutDirective.DefaultWorkspaceLayout);

        var invalid = I3ConfigParser.Parse("workspace_layout stacking");
        Assert.Equal(LayoutMode.Split, invalid.DefaultWorkspaceLayout);
        Assert.Single(invalid.Warnings);
    }

    [Fact]
    public void Min_window_width_defaults_to_400_without_the_directive()
    {
        var config = I3ConfigParser.Parse("""
            bindsym Mod4+q kill
            """);

        Assert.Equal(400, config.MinWindowWidth);
    }

    [Fact]
    public void Min_window_width_takes_variables_and_rejects_invalid_values()
    {
        var withVariable = I3ConfigParser.Parse("""
            set $minw 300
            min_window_width $minw
            """);
        Assert.Equal(300, withVariable.MinWindowWidth);
        Assert.Empty(withVariable.Warnings);

        var invalid = I3ConfigParser.Parse("""
            min_window_width narrow
            """);
        Assert.Equal(400, invalid.MinWindowWidth);
        Assert.Single(invalid.Warnings);

        var nonPositive = I3ConfigParser.Parse("""
            min_window_width 0
            """);
        Assert.Equal(400, nonPositive.MinWindowWidth);
        Assert.Single(nonPositive.Warnings);
    }

    [Fact]
    public void Bar_block_collects_the_configuration()
    {
        var config = I3ConfigParser.Parse("""
            bar {
              position bottom
              height 40
              clock yes
              date no
              cpu yes
              memory yes
              exec powershell -c "Get-Random"
            }
            """);

        var bar = config.Bar!;
        Assert.Equal("bottom", bar.Position);
        Assert.Equal(40, bar.Height);
        Assert.True(bar.Clock);
        Assert.False(bar.Date);
        Assert.True(bar.Cpu);
        Assert.True(bar.Memory);
        var command = Assert.Single(bar.Commands);
        Assert.Equal("powershell -c \"Get-Random\"", command);
        Assert.Empty(config.Warnings);
    }

    [Fact]
    public void Bar_defaults_without_a_block()
    {
        var config = I3ConfigParser.Parse("""
            bindsym Mod4+1 workspace 1
            """);

        var bar = config.Bar!;
        Assert.Equal("top", bar.Position);
        Assert.Equal(30, bar.Height);
        Assert.False(bar.Clock);
        Assert.False(bar.Date);
        Assert.False(bar.Cpu);
        Assert.False(bar.Memory);
        Assert.Empty(bar.Commands);
    }

    [Fact]
    public void Bar_block_rejects_unknown_directives_and_bad_values()
    {
        var config = I3ConfigParser.Parse("""
            bar {
                position sideways
                height 500
                clock maybe
                frobnicate yes
            }
            """);

        Assert.Equal("top", config.Bar!.Position);
        Assert.Equal(30, config.Bar!.Height);
        Assert.False(config.Bar!.Clock);
        Assert.Equal(4, config.Warnings.Count);
    }
}
