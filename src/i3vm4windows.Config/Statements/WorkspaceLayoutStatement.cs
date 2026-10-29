using i3vm4windows.Core;

namespace i3vm4windows.Config.Statements;

/// <summary>
/// workspace_layout tabbed|default: the layout new workspaces start
/// in — i3's tabbed default instead of tiling. Parse-time data, like
/// min_window_width, not a runtime action: it flows into the config
/// and the app hands it to the window manager at startup.
/// </summary>
public sealed class WorkspaceLayoutStatement : TopLevelStatement
{
    public override string Name => "workspace_layout";

    protected override void Apply(ConfigContext context, IReadOnlyList<string> parameters)
    {
        if (parameters.Count == 1)
        {
            var mode = parameters[0].ToLowerInvariant() switch
            {
                "tabbed" => LayoutMode.Tabbed,
                "default" or "split" => LayoutMode.Split,
                _ => (LayoutMode?)null,
            };
            if (mode is { } layout)
            {
                context.WorkspaceLayout = layout;
                return;
            }
        }

        context.Warn($"Unsupported workspace_layout: {context.Line}");
    }
}
