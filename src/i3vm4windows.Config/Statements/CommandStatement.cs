namespace i3vm4windows.Config.Statements;

/// <summary>
/// The commands a bindsym nests and a for_window applies to the windows
/// it matches: each one resolves from the IoC container by its name and
/// writes its intent into the context for the statement that collected
/// it.
/// </summary>
public abstract class CommandStatement : IConfigStatement
{
    public abstract string Name { get; }

    public abstract void Execute(ConfigContext context, IReadOnlyList<string> parameters);
}
