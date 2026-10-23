using System.Text.RegularExpressions;

namespace i3vm4windows.Config;

/// <summary>
/// Parses the key bindings of an i3 configuration file: comment lines,
/// blank lines, backslash line continuations, <c>set $name value</c>
/// variables and <c>bindsym keys command</c> directives, for_window rules
/// and exec autostart directives. A subset of i3 commands maps to the
/// app's tiling actions; anything the app cannot perform produces a
/// warning and is ignored, as are other i3 directives (font, bar, gaps,
/// ...) that have no meaning here.
/// </summary>
public static class I3ConfigParser
{
    /// <summary>i3 allows numbered workspace names like "1: web".</summary>
    private static readonly Regex NumberedWorkspace = new(@"^(\d+)\s*:", RegexOptions.Compiled);

    public static I3Config Parse(string text)
    {
        var warnings = new List<string>();
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Later bindsym lines win over earlier ones for the same key
        // combination, like i3 replaces bindings; the mode is part of the
        // key so default and mode bindings coexist.
        var bindings = new Dictionary<(string Mode, Modifiers Modifiers, string Key), Binding>();
        var rules = new List<WindowRule>();
        var exec = new List<string>();
        string? mode = null;

        foreach (var original in JoinContinuations(text))
        {
            var line = StripComment(original).Trim();
            if (line.Length == 0)
                continue;

            var tokens = Tokenize(line);
            if (tokens.Length == 0)
                continue;

            // mode "name" { opens a binding mode block; } closes it. The
            // block's bindsym lines belong to the mode until then.
            if (tokens[0].Equals("mode", StringComparison.OrdinalIgnoreCase))
            {
                if (mode is not null)
                {
                    warnings.Add($"Nested mode block: {line}");
                    continue;
                }

                if (tokens.Length == 3 && tokens[2] == "{")
                {
                    mode = tokens[1].Trim('"');
                    continue;
                }

                warnings.Add($"Unsupported mode directive: {line}");
                continue;
            }

            if (mode is not null && tokens[0] == "}")
            {
                mode = null;
                continue;
            }

            // set stores its value verbatim; variables are substituted where
            // they are used, not on the set line itself.
            if (tokens[0].Equals("set", StringComparison.OrdinalIgnoreCase))
            {
                if (tokens.Length < 3 || !tokens[1].StartsWith('$'))
                {
                    warnings.Add($"Unsupported set directive: {line}");
                    continue;
                }

                var value = string.Join(' ', tokens.Skip(2));
                if (value.Length >= 2 && value.StartsWith('"') && value.EndsWith('"'))
                    value = value[1..^1];
                variables[tokens[1]] = value;
                continue;
            }

            line = SubstituteVariables(line, variables);
            tokens = Tokenize(line);
            if (tokens.Length == 0)
                continue;

            switch (tokens[0].ToLowerInvariant())
            {
                case "bindsym":
                    ParseBindsym(tokens, bindings, warnings, line, mode);
                    break;

                case "for_window":
                    ParseForWindow(tokens, rules, warnings, line);
                    break;

                case "exec":
                    ParseExec(tokens, exec, warnings, line);
                    break;

                default:
                    if (tokens[0].Equals("bindcode", StringComparison.OrdinalIgnoreCase))
                        warnings.Add($"bindcode is not supported: {line}");
                    break;
            }
        }

        return new I3Config(bindings.Values.ToList(), warnings, rules, exec);
    }

    /// <summary>
    /// Parses an exec directive, i3's autostart: the program starts when
    /// the app starts with this configuration. The command keeps its
    /// original case and quoting, like the exec commands of bindings.
    /// </summary>
    private static void ParseExec(
        string[] tokens, List<string> exec, List<string> warnings, string line)
    {
        var rest = tokens[1..];
        if (rest.Length > 0 &&
            rest[0].Equals("--no-startup-id", StringComparison.OrdinalIgnoreCase))
        {
            rest = rest[1..];
        }

        if (rest.Length == 0)
        {
            warnings.Add($"exec without a program: {line}");
            return;
        }

        exec.Add(string.Join(' ', rest));
    }

    /// <summary>
    /// Parses a for_window directive, e.g.
    /// for_window [class="Chrome_WidgetWin_1" title="^Vivaldi$"] floating enable.
    /// The criteria match a window's class and title against regular
    /// expressions like i3 does; floating enable leaves matching windows
    /// alone and move container to workspace makes them join that
    /// workspace when they appear.
    /// </summary>
    private static void ParseForWindow(
        string[] tokens,
        List<WindowRule> rules,
        List<string> warnings,
        string line)
    {
        var index = 1;
        if (index >= tokens.Length || !tokens[index].StartsWith('['))
        {
            warnings.Add($"for_window without criteria: {line}");
            return;
        }

        if (!TryParseCriteria(tokens, ref index, out var rule, warnings, line))
            return;

        var command = tokens[(index + 1)..].Select(t => t.ToLowerInvariant()).ToArray();
        if (command.Length == 0)
        {
            warnings.Add($"for_window without a command: {line}");
            return;
        }

        switch (command)
        {
            case ["floating", "enable"]:
                rules.Add(rule!);
                break;

            case ["move", "container", "to", "workspace", ..]:
            case ["move", "to", "workspace", ..]:
                // Workspace names may span several tokens, e.g. "3: web".
                var nameIndex = command[1] == "container" ? 4 : 3;
                if (!TryParseWorkspace(string.Join(' ', command[nameIndex..]), out var workspace))
                {
                    warnings.Add($"Unsupported workspace in for_window: {line}");
                    break;
                }

                rules.Add(rule! with
                {
                    Action = WindowRuleAction.MoveToWorkspace,
                    Parameter = workspace,
                });
                break;

            default:
                warnings.Add($"Unsupported for_window command: {line}");
                break;
        }
    }

    /// <summary>
    /// Parses a [class="..." title="..."] criteria section starting at
    /// <paramref name="index"/>; on success the index ends on the token
    /// that closes the bracket and the rule carries the expressions.
    /// </summary>
    private static bool TryParseCriteria(
        string[] tokens,
        ref int index,
        out WindowRule? rule,
        List<string> warnings,
        string line)
    {
        rule = null;
        var criteria = tokens[index];
        while (!criteria.EndsWith(']'))
        {
            index++;
            if (index >= tokens.Length)
            {
                warnings.Add($"Unterminated criteria: {line}");
                return false;
            }

            criteria += " " + tokens[index];
        }

        Regex? classRegex = null;
        Regex? titleRegex = null;
        foreach (Match match in Regex.Matches(criteria, @"(class|title)\s*=\s*""([^""]*)"""))
        {
            var regex = TryCreateRegex(match.Groups[2].Value, warnings, line);
            if (regex is null)
                continue;

            if (match.Groups[1].Value.Equals("class", StringComparison.OrdinalIgnoreCase))
                classRegex = regex;
            else
                titleRegex = regex;
        }

        if (classRegex is null && titleRegex is null)
        {
            warnings.Add($"Criteria without class or title: {line}");
            return false;
        }

        rule = new WindowRule(classRegex, titleRegex);
        return true;
    }

    private static Regex? TryCreateRegex(string pattern, List<string> warnings, string line)
    {
        try
        {
            return new Regex(pattern, RegexOptions.IgnoreCase);
        }
        catch (ArgumentException)
        {
            warnings.Add($"Invalid regular expression in {line}");
            return null;
        }
    }

    private static void ParseBindsym(
        string[] tokens,
        Dictionary<(string Mode, Modifiers Modifiers, string Key), Binding> bindings,
        List<string> warnings,
        string line,
        string? mode)
    {
        if (tokens.Length > 1 && tokens[1].Equals("--release", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add($"Release-time bindings are not supported: {line}");
            return;
        }

        if (tokens.Length < 3)
        {
            warnings.Add($"Incomplete bindsym: {line}");
            return;
        }

        var keys = tokens[1];
        var original = tokens[2..];

        // A criteria prefix targets windows, e.g.
        // [class="CASCADIA_HOSTING_WINDOW_CLASS"] scratchpad show.
        var index = 0;
        WindowRule? rule = null;
        if (original.Length > 0 && original[0].StartsWith('['))
        {
            if (!TryParseCriteria(original, ref index, out rule, warnings, line))
                return;

            // The criteria ended on the token at index; the command follows.
            index++;
        }

        var originalCommand = original[index..];
        var command = originalCommand.Select(t => t.ToLowerInvariant()).ToArray();
        if (command.Length == 0)
        {
            warnings.Add($"bindsym without a command: {line}");
            return;
        }

        if (!TryParseKeys(keys, out var modifiers, out var key))
        {
            warnings.Add($"Unsupported key combination: {line}");
            return;
        }

        // The hook dispatches combinations through its ownership of the Win
        // key, so a default-mode binding without Win has no reliable meaning
        // here; mode bindings dispatch through the hook's mode matching
        // instead.
        if (mode is null && !modifiers.HasFlag(Modifiers.Win))
        {
            warnings.Add($"Binding must include the Win key (Mod4): {line}");
            return;
        }

        if (!TryParseCommand(command, originalCommand, out var action, out var parameter, out var commandText))
        {
            warnings.Add($"Unsupported command: {line}");
            return;
        }

        // The stateless scratchpad toggle needs to know which window to act
        // on; criteria are how the configuration expresses that.
        if (action == ConfigAction.ScratchpadShow && rule is null)
        {
            warnings.Add($"scratchpad show needs window criteria: {line}");
            return;
        }

        bindings[(mode ?? "default", modifiers, key)] =
            new Binding(modifiers, key, action, parameter, commandText, rule, mode ?? "default");
    }

    /// <summary>
    /// Splits a key combination like "$mod+Shift+1" into its modifiers and
    /// the normalized key name.
    /// </summary>
    private static bool TryParseKeys(string keys, out Modifiers modifiers, out string key)
    {
        modifiers = Modifiers.None;
        var parts = keys.Split('+');

        foreach (var part in parts[..^1])
        {
            switch (part.ToLowerInvariant())
            {
                case "mod1" or "alt":
                    modifiers |= Modifiers.Alt;
                    break;
                case "mod4" or "win" or "super":
                    modifiers |= Modifiers.Win;
                    break;
                case "shift":
                    modifiers |= Modifiers.Shift;
                    break;
                case "ctrl" or "control":
                    modifiers |= Modifiers.Control;
                    break;
                default:
                    key = "";
                    return false;
            }
        }

        key = NormalizeKey(parts[^1]);
        return key is not null;
    }

    /// <summary>Normalizes an i3 key name to a canonical one, or null.</summary>
    private static string? NormalizeKey(string name)
    {
        if (name.Length == 1)
        {
            if (char.IsAsciiDigit(name[0]))
                return name;
            if (char.IsAsciiLetter(name[0]))
                return char.ToUpperInvariant(name[0]).ToString();
        }

        return name.ToLowerInvariant() switch
        {
            "left" => "Left",
            "right" => "Right",
            "up" => "Up",
            "down" => "Down",
            "enter" or "return" => "Enter",
            "space" => "Space",
            "tab" => "Tab",
            "escape" => "Escape",
            _ => null,
        };
    }

    private static bool TryParseCommand(
        string[] command,
        string[] original,
        out ConfigAction action,
        out int parameter,
        out string? commandText)
    {
        action = default;
        parameter = 0;
        commandText = null;

        switch (command)
        {
            case ["exec", ..]:
                // The program name and arguments keep their original case;
                // only the --no-startup-id marker of i3 is dropped.
                var execRest = command[1..];
                var execOriginal = original[1..];
                var offset = execRest.Length > 0 &&
                    execRest[0].Equals("--no-startup-id", StringComparison.Ordinal)
                        ? 1
                        : 0;
                if (execRest.Length - offset == 0)
                    return false;

                commandText = string.Join(' ', execOriginal[offset..]);
                action = ConfigAction.Launch;
                return true;
            case ["kill"]:
                action = ConfigAction.CloseFocused;
                return true;
            case ["focus", "left"]:
                action = ConfigAction.FocusPrevious;
                return true;
            case ["focus", "right"]:
                action = ConfigAction.FocusNext;
                return true;
            case ["move", "left"]:
                action = ConfigAction.MovePrevious;
                return true;
            case ["move", "right"]:
                action = ConfigAction.MoveNext;
                return true;
            case ["split", "toggle"]:
            case ["layout", "toggle", "split"]:
                action = ConfigAction.ToggleOrientation;
                return true;
            case ["layout", "tabbed"]:
                action = ConfigAction.SetTabbed;
                return true;
            case ["layout", "default"]:
                action = ConfigAction.SetSplit;
                return true;
            case ["floating", "toggle"]:
                action = ConfigAction.FloatingToggle;
                return true;
            case ["scratchpad", "show"]:
                action = ConfigAction.ScratchpadShow;
                return true;
            case ["workspace", "back_and_forth"]:
                action = ConfigAction.SwitchBackAndForth;
                return true;
            case ["mode", var name]:
                action = ConfigAction.Mode;
                commandText = name.Trim('"');
                return true;
            case ["resize", "grow", "width", ..]:
            case ["resize", "shrink", "width", ..]:
            case ["resize", "grow", "height", ..]:
            case ["resize", "shrink", "height", ..]:
                // The pixel amount drives the resize; ppt (percentage
                // points) has no meaning here.
                if (command.Length < 4 || !int.TryParse(command[3], out parameter))
                    return false;

                action = command[1] == "grow"
                    ? command[2] == "width" ? ConfigAction.ResizeGrowWidth : ConfigAction.ResizeGrowHeight
                    : command[2] == "width" ? ConfigAction.ResizeShrinkWidth : ConfigAction.ResizeShrinkHeight;
                return true;
            case ["reload"]:
                action = ConfigAction.Refresh;
                return true;
            // Workspace names may span several tokens, e.g. "1: web".
            case ["workspace", .. var rest] when TryParseWorkspace(string.Join(' ', rest), out parameter):
                action = ConfigAction.SwitchWorkspace;
                return true;
            case ["move", "container", "to", "workspace", .. var rest]
                when TryParseWorkspace(string.Join(' ', rest), out parameter):
                action = ConfigAction.MoveToWorkspace;
                return true;
            case ["move", "workspace", "to", "output", "next"]:
                action = ConfigAction.MoveWorkspaceToNextMonitor;
                return true;
            default:
                return false;
        }
    }

    private static bool TryParseWorkspace(string name, out int index)
    {
        index = 0;
        if (int.TryParse(name, out index))
            return true;

        var match = NumberedWorkspace.Match(name);
        return match.Success && int.TryParse(match.Groups[1].Value, out index);
    }

    private static string SubstituteVariables(
        string line, Dictionary<string, string> variables)
    {
        // Longest names first so $ws10 is not replaced inside $ws1.
        foreach (var (name, value) in variables.OrderByDescending(v => v.Key.Length))
            line = line.Replace(name, value, StringComparison.OrdinalIgnoreCase);

        return line;
    }

    private static IEnumerable<string> JoinContinuations(string text)
    {
        var pending = "";
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.EndsWith('\\'))
            {
                pending += line[..^1] + " ";
                continue;
            }

            yield return pending + line;
            pending = "";
        }

        if (pending.Length > 0)
            yield return pending;
    }

    /// <summary>
    /// Splits a line on whitespace, but a token wrapped in double quotes
    /// keeps its spaces (program paths in exec commands, values in set).
    /// </summary>
    private static string[] Tokenize(string line)
    {
        var tokens = new List<string>();
        var index = 0;
        while (index < line.Length)
        {
            while (index < line.Length && char.IsWhiteSpace(line[index]))
                index++;
            if (index >= line.Length)
                break;

            var start = index;
            if (line[index] == '"')
            {
                index++;
                while (index < line.Length && line[index] != '"')
                    index++;
                if (index < line.Length)
                    index++;
            }
            else
            {
                while (index < line.Length && !char.IsWhiteSpace(line[index]))
                    index++;
            }

            tokens.Add(line[start..index]);
        }

        return tokens.ToArray();
    }

    private static string StripComment(string line)
    {
        var start = line.IndexOf('#');
        return start < 0 ? line : line[..start];
    }
}
