namespace i3vm4windows.Config.Statements;

/// <summary>
/// }: closes the open bar or mode block, as the parser knows nothing
/// about blocks — and a stray one warns, there being nothing to close.
/// </summary>
public sealed class CloseBlockStatement : IConfigStatement
{
    public string Name => "}";

    public void Execute(ConfigContext context, IReadOnlyList<string> parameters)
    {
        if (parameters.Count != 0)
        {
            context.Warn($"Unsupported block closing: {context.Line}");
            return;
        }

        if (context.InBar)
        {
            context.InBar = false;
            return;
        }

        if (context.Mode is not null)
        {
            context.Mode = null;
            return;
        }

        context.Warn($"No block to close: {context.Line}");
    }
}
