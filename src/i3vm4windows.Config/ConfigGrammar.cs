using System.Text.RegularExpressions;

namespace i3vm4windows.Config;

/// <summary>
/// The grammar pieces statements share: key combinations, workspace
/// names and the window criteria sections.
/// </summary>
internal static class ConfigGrammar
{
    /// <summary>i3 allows numbered workspace names like "1: web".</summary>
    private static readonly Regex NumberedWorkspace = new(@"^(\d+)\s*:", RegexOptions.Compiled);

    /// <summary>The parameters from the given index on, as a copy.</summary>
    public static string[] Rest(IReadOnlyList<string> parameters, int from)
    {
        var rest = new string[parameters.Count - from];
        for (var i = 0; i < rest.Length; i++)
            rest[i] = parameters[from + i];
        return rest;
    }

    /// <summary>
    /// Splits a key combination like "$mod+Shift+1" into its modifiers and
    /// the normalized key name.
    /// </summary>
    public static bool TryParseKeys(string keys, out Modifiers modifiers, out string key)
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
    public static string? NormalizeKey(string name)
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

    public static bool TryParseWorkspace(string name, out int index)
    {
        index = 0;
        if (int.TryParse(name, out index))
            return true;

        var match = NumberedWorkspace.Match(name);
        return match.Success && int.TryParse(match.Groups[1].Value, out index);
    }

    /// <summary>
    /// Parses a [class="..." title="..."] criteria section starting at
    /// <paramref name="index"/>; on success the index ends on the token
    /// that closes the bracket and the rule carries the expressions.
    /// </summary>
    public static bool TryParseCriteria(
        IReadOnlyList<string> parameters,
        ref int index,
        out WindowRule? rule,
        ConfigContext context)
    {
        rule = null;
        var criteria = parameters[index];
        while (!criteria.EndsWith(']'))
        {
            index++;
            if (index >= parameters.Count)
            {
                context.Warn($"Unterminated criteria: {context.Line}");
                return false;
            }

            criteria += " " + parameters[index];
        }

        Regex? classRegex = null;
        Regex? titleRegex = null;
        foreach (Match match in Regex.Matches(criteria, @"(class|title)\s*=\s*""([^""]*)"""))
        {
            var regex = TryCreateRegex(match.Groups[2].Value, context);
            if (regex is null)
                continue;

            if (match.Groups[1].Value.Equals("class", StringComparison.OrdinalIgnoreCase))
                classRegex = regex;
            else
                titleRegex = regex;
        }

        if (classRegex is null && titleRegex is null)
        {
            context.Warn($"Criteria without class or title: {context.Line}");
            return false;
        }

        rule = new WindowRule(classRegex, titleRegex);
        return true;
    }

    private static Regex? TryCreateRegex(string pattern, ConfigContext context)
    {
        try
        {
            return new Regex(pattern, RegexOptions.IgnoreCase);
        }
        catch (ArgumentException)
        {
            context.Warn($"Invalid regular expression in {context.Line}");
            return null;
        }
    }
}
