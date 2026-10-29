namespace i3vm4windows.Config.Statements;

/// <summary>bindcode: not supported, and says so.</summary>
public sealed class BindcodeStatement : TopLevelStatement
{
    public override string Name => "bindcode";

    protected override void Apply(ConfigContext context, IReadOnlyList<string> parameters) =>
        context.Warn($"bindcode is not supported: {context.Line}");
}
