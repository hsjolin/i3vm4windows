namespace i3vm4windows.Config.Statements;

/// <summary>bar { ... }: opens the bar block.</summary>
public sealed class BarStatement : IConfigStatement
{
    public string Name => "bar";

    public void Execute(ConfigContext context, IReadOnlyList<string> parameters)
    {
        if (parameters.Count == 1 && parameters[0] == "{")
        {
            if (context.Mode is not null || context.InBar)
            {
                context.Warn($"Nested block: {context.Line}");
                return;
            }

            context.InBar = true;
            return;
        }

        context.Warn($"Unsupported bar directive: {context.Line}");
    }
}
