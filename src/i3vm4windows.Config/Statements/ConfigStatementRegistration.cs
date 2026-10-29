using Microsoft.Extensions.DependencyInjection;

namespace i3vm4windows.Config.Statements;

/// <summary>
/// Registers every built-in statement, so parses resolve them by name
/// from the container. Adding a statement means implementing
/// <see cref="IConfigStatement"/> and adding a line here.
/// </summary>
public static class ConfigStatementRegistration
{
    public static IServiceCollection AddConfigStatements(this IServiceCollection services)
    {
        services.AddSingleton<IConfigStatement, BindsymStatement>();
        services.AddSingleton<IConfigStatement, ForWindowStatement>();
        services.AddSingleton<IConfigStatement, SetStatement>();
        services.AddSingleton<IConfigStatement, ExecStatement>();
        services.AddSingleton<IConfigStatement, MinWindowWidthStatement>();
        services.AddSingleton<IConfigStatement, BindcodeStatement>();
        services.AddSingleton<IConfigStatement, ModeStatement>();
        services.AddSingleton<IConfigStatement, BarStatement>();
        services.AddSingleton<IConfigStatement, CloseBlockStatement>();
        services.AddSingleton<IConfigStatement, BarPositionStatement>();
        services.AddSingleton<IConfigStatement, BarHeightStatement>();
        services.AddSingleton<IConfigStatement>(new BarFlagStatement(
            "clock", (bar, value) => bar.Clock = value));
        services.AddSingleton<IConfigStatement>(new BarFlagStatement(
            "date", (bar, value) => bar.Date = value));
        services.AddSingleton<IConfigStatement>(new BarFlagStatement(
            "cpu", (bar, value) => bar.Cpu = value));
        services.AddSingleton<IConfigStatement>(new BarFlagStatement(
            "memory", (bar, value) => bar.Memory = value));
        services.AddSingleton<IConfigStatement, KillStatement>();
        services.AddSingleton<IConfigStatement, FocusStatement>();
        services.AddSingleton<IConfigStatement, MoveStatement>();
        services.AddSingleton<IConfigStatement, SplitStatement>();
        services.AddSingleton<IConfigStatement, LayoutStatement>();
        services.AddSingleton<IConfigStatement, FloatingStatement>();
        services.AddSingleton<IConfigStatement, ScratchpadStatement>();
        services.AddSingleton<IConfigStatement, WorkspaceStatement>();
        services.AddSingleton<IConfigStatement, ResizeStatement>();
        services.AddSingleton<IConfigStatement, ReloadStatement>();
        return services;
    }
}
