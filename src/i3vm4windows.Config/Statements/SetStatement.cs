namespace i3vm4windows.Config.Statements;

/// <summary>
/// set $name value: stores a variable verbatim, substituted where it is
/// used, not on the set line itself.
/// </summary>
public sealed class SetStatement : TopLevelStatement
{
    public override string Name => "set";

    protected override void Apply(ConfigContext context, IReadOnlyList<string> parameters)
    {
        if (parameters.Count < 2 || !parameters[0].StartsWith('$'))
        {
            context.Warn($"Unsupported set directive: {context.Line}");
            return;
        }

        var value = string.Join(' ', ConfigGrammar.Rest(parameters, 1));
        if (value.Length >= 2 && value.StartsWith('"') && value.EndsWith('"'))
            value = value[1..^1];
        context.Variables[parameters[0]] = value;
    }
}
