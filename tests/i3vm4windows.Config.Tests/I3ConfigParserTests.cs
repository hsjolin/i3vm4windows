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
            bindsym Mod4+e exec thunar
            bindsym Mod4+F1 nop
            bindsym Mod4+b workspace browsing
            bindsym Mod4+XF86AudioRaiseVolume nop
            bindsym Ctrl+x kill
            """);

        Assert.Empty(config.Bindings);
        Assert.Equal(5, config.Warnings.Count);
        Assert.Contains(config.Warnings, w => w.Contains("Unsupported command"));
        Assert.Contains(config.Warnings, w => w.Contains("Unsupported key combination"));
        Assert.Contains(config.Warnings, w => w.Contains("Win key"));
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
        Assert.Equal(26, config.Bindings.Count);

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
}
