using i3vm4windows.Config;
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
    public void Non_binding_directives_are_ignored_silently()
    {
        var config = I3ConfigParser.Parse("""
            font pango:monospace 8
            gaps inner 8
            exec --no-startup-id nm-applet
            bar {
                status_command i3status
            }
            """);

        Assert.Empty(config.Bindings);
        Assert.Empty(config.Warnings);
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
        Assert.Equal(27, config.Bindings.Count);

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
}
