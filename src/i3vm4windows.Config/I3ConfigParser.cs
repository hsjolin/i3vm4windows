using System.Text.RegularExpressions;

namespace i3vm4windows.Config;

/// <summary>
/// Parses the key bindings of an i3 configuration file: comment lines,
/// blank lines, backslash line continuations, <c>set $name value</c>
/// variables and <c>bindsym keys command</c> directives. A subset of i3
/// commands maps to the app's tiling actions; anything the app cannot
/// perform produces a warning and is ignored, as are other i3 directives
/// (font, bar, gaps, exec, ...) that have no meaning here.
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
        // combination, like i3 replaces bindings.
        var bindings = new Dictionary<(Modifiers Modifiers, string Key), Binding>();
        var rules = new List<WindowRule>();

        foreach (var original in JoinContinuations(text))
        {
            var line = StripComment(original).Trim();
            if (line.Length == 0)
                continue;

            var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0)
                continue;

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
            tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0)
                continue;

            switch (tokens[0].ToLowerInvariant())
            {
                case "bindsym":
                    ParseBindsym(tokens, bindings, warnings, line);
                    break;

                case "for_window":
                    ParseForWindow(tokens, rules, warnings, line);
                    break;

                default:
                    if (tokens[0].Equals("bindcode", StringComparison.OrdinalIgnoreCase))
                        warnings.Add($"bindcode is not supported: {line}");
                    break;
            }
        }

        return new I3Config(bindings.Values.ToList(), warnings, rules);
    }

    /// <summary>
    /// Parses a for_window directive, e.g.
    /// for_window [class="Chrome_WidgetWin_1" title="^Vivaldi$"] floating enable.
    /// The criteria match a window's class and title against regular
    /// expressions like i3 does; floating enable is the only supported
    /// command.
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

        var criteria = tokens[index];
        while (!criteria.EndsWith(']'))
        {
            index++;
            if (index >= tokens.Length)
            {
                warnings.Add($"Unterminated for_window criteria: {line}");
                return;
            }

            criteria += " " + tokens[index];
        }

        var command = tokens[(index + 1)..];
        if (command.Length == 0)
        {
            warnings.Add($"for_window without a command: {line}");
            return;
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
            warnings.Add($"for_window without class or title criteria: {line}");
            return;
        }

        switch (command)
        {
            case ["floating", "enable"]:
                rules.Add(new WindowRule(classRegex, titleRegex));
                break;
            default:
                warnings.Add($"Unsupported for_window command: {line}");
                break;
        }
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
        Dictionary<(Modifiers Modifiers, string Key), Binding> bindings,
        List<string> warnings,
        string line)
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

        var command = tokens[2..].Select(t => t.ToLowerInvariant()).ToArray();

        if (!TryParseKeys(tokens[1], out var modifiers, out var key))
        {
            warnings.Add($"Unsupported key combination: {line}");
            return;
        }

        // The hook dispatches combinations through its ownership of the Win
        // key, so a binding without Win has no reliable meaning here.
        if (!modifiers.HasFlag(Modifiers.Win))
        {
            warnings.Add($"Binding must include the Win key (Mod4): {line}");
            return;
        }

        if (TryParseCommand(command, out var action, out var parameter))
            bindings[(modifiers, key)] = new Binding(modifiers, key, action, parameter);
        else
            warnings.Add($"Unsupported command: {line}");
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

    private static bool TryParseCommand(string[] command, out ConfigAction action, out int parameter)
    {
        action = default;
        parameter = 0;

        switch (command)
        {
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

    private static string StripComment(string line)
    {
        var start = line.IndexOf('#');
        return start < 0 ? line : line[..start];
    }
}
