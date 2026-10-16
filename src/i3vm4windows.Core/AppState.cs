namespace i3vm4windows.Core;

/// <summary>A remembered window placement, keyed by process image and title.</summary>
public sealed record WindowState(string ExePath, string Title, int Workspace)
{
    public bool Matches(WindowState other) =>
        string.Equals(ExePath, other.ExePath, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Title, other.Title, StringComparison.Ordinal);
}

/// <summary>
/// Persisted session state: which workspace was active on each monitor and
/// which workspace each managed window was last tiled on.
/// </summary>
public sealed class AppState
{
    /// <summary>Display device name -> the workspace that was active on it.</summary>
    public Dictionary<string, int> ActiveWorkspaceByMonitor { get; set; } = new();

    /// <summary>Workspace number -> the display it is bound to.</summary>
    public Dictionary<int, string> OutputByWorkspace { get; set; } = new();

    /// <summary>Known window placements, most recent first.</summary>
    public List<WindowState> Windows { get; set; } = new();

    /// <summary>
    /// Finds the workspace for a window identity: an exact image-and-title
    /// match wins, then the first placement with the same process image.
    /// </summary>
    public int? ResolveWorkspace(string exePath, string title)
    {
        var exact = ResolveWorkspaceExact(exePath, title);
        if (exact is not null)
            return exact;

        if (!string.IsNullOrEmpty(exePath))
        {
            var byImage = Windows.FirstOrDefault(w =>
                string.Equals(w.ExePath, exePath, StringComparison.OrdinalIgnoreCase));
            if (byImage is not null)
                return byImage.Workspace;
        }

        return null;
    }

    /// <summary>
    /// Requires an exact image-and-title match, without the process-image
    /// fallback. Used to adopt windows that a previous instance left hidden.
    /// </summary>
    public int? ResolveWorkspaceExact(string exePath, string title) =>
        Windows.FirstOrDefault(w => w.Matches(new WindowState(exePath, title, 0)))?.Workspace;

    /// <summary>
    /// Keeps placements from a previous state that were not seen again, so
    /// closed windows are still recognized after a restart, and trims the
    /// list to the <paramref name="maxRememberedWindows"/> most recent
    /// entries (current placements come first).
    /// </summary>
    public void MergeRemembered(AppState? previous, int maxRememberedWindows = 300)
    {
        if (previous is not null)
        {
            foreach (var remembered in previous.Windows)
            {
                if (!Windows.Any(w => w.Matches(remembered)))
                    Windows.Add(remembered);
            }

            foreach (var (device, active) in previous.ActiveWorkspaceByMonitor)
            {
                if (ActiveWorkspaceByMonitor.Keys.All(k =>
                        !string.Equals(k, device, StringComparison.OrdinalIgnoreCase)))
                    ActiveWorkspaceByMonitor[device] = active;
            }

            foreach (var (index, output) in previous.OutputByWorkspace)
            {
                if (!OutputByWorkspace.ContainsKey(index))
                    OutputByWorkspace[index] = output;
            }
        }

        if (Windows.Count > maxRememberedWindows)
            Windows = Windows.Take(maxRememberedWindows).ToList();
    }
}
