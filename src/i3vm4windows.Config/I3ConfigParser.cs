using i3vm4windows.Config.Statements;
using Microsoft.Extensions.DependencyInjection;

namespace i3vm4windows.Config;

/// <summary>
/// Parses an i3 configuration file into statements: comment lines, blank
/// lines and backslash line continuations are folded away and
/// <c>set</c> variables substituted, then every line resolves its
/// statement — bindsym, exec, for_window, the bar block's directives —
/// from the IoC container by name and executes it. A bindsym's command
/// nests: it resolves to a statement of its own whose intent becomes
/// the binding's action. Unknown statements warn.
/// </summary>
public static class I3ConfigParser
{
    public static I3Config Parse(string text) => Parse(text, (IServiceCollection?)null);

    /// <summary>
    /// Parses with the statements of the given collection plus every
    /// built-in one, so callers add their own statements by registering
    /// IConfigStatement implementations.
    /// </summary>
    public static I3Config Parse(string text, IServiceCollection? services)
    {
        var collection = services ?? new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        collection.AddConfigStatements();
        using var provider = collection.BuildServiceProvider();
        return Parse(text, provider.GetServices<IConfigStatement>());
    }

    public static I3Config Parse(string text, IEnumerable<IConfigStatement> statements)
    {
        var context = new ConfigContext(statements);

        foreach (var original in JoinContinuations(text))
        {
            var line = StripComment(original).Trim();
            if (line.Length == 0)
                continue;

            var tokens = Tokenize(line);
            if (tokens.Length == 0)
                continue;

            // set stores its value verbatim, so its statement runs on the
            // unsubstituted line: substituting would rewrite a variable's
            // own name when it is redefined. Everything else resolves
            // after substitution — the closing braces included, as the
            // block state itself lives in the statements.
            if (!tokens[0].Equals("set", StringComparison.OrdinalIgnoreCase))
            {
                line = SubstituteVariables(line, context.Variables);
                tokens = Tokenize(line);
                if (tokens.Length == 0)
                    continue;
            }

            context.Line = line;

            var statement = context.Resolve(tokens[0]);
            if (statement is null)
            {
                context.Warn($"Unknown statement: {line}");
                continue;
            }

            statement.Execute(context, tokens[1..]);
        }

        return new I3Config(
            context.Bindings.Values.ToList(),
            context.Warnings,
            context.Rules,
            context.Autostarts,
            context.MinWindowWidth,
            context.WorkspaceLayout,
            context.Bar.ToConfig());
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
