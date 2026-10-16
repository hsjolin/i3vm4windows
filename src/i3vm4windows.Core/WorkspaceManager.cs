namespace i3vm4windows.Core;

/// <summary>One output's view change: from PreviousIndex to NewIndex.</summary>
public sealed record OutputSwitch(string Output, int PreviousIndex, int NewIndex)
{
    public bool Changed => PreviousIndex != NewIndex;
}

/// <summary>The result of switching workspaces: every output whose view changed.</summary>
public sealed record SwitchResult(IReadOnlyList<OutputSwitch> Changes);

/// <summary>The result of moving a workspace to another output.</summary>
public sealed record WorkspaceMoveResult(
    int MovedIndex, string SourceOutput, int SourceReplacementIndex, string TargetOutput);

/// <summary>The result of moving a window to another workspace.</summary>
public sealed record WindowMoveResult(long WindowHandle, Workspace Target);

/// <summary>
/// Owns the global set of numbered workspaces, tracks which one is active
/// per output and moves windows and workspaces between outputs. Outputs are
/// identified by name, e.g. a display device name. All state is plain data,
/// so the whole class is unit-testable without Windows.
/// </summary>
public sealed class WorkspaceManager
{
    private readonly Dictionary<int, Workspace> _workspaces = new();
    private readonly Dictionary<string, int> _activeByOutput = new(StringComparer.OrdinalIgnoreCase);
    private readonly TilingEngine _engine = new();

    /// <summary>All known workspaces.</summary>
    public IReadOnlyCollection<Workspace> All => _workspaces.Values;

    /// <summary>Workspace number -> the output it is bound to.</summary>
    public IReadOnlyDictionary<int, string> OutputBindings =>
        _workspaces.ToDictionary(pair => pair.Key, pair => pair.Value.OutputName);

    public Workspace? TryGet(int index) => _workspaces.GetValueOrDefault(index);

    /// <summary>
    /// Returns the workspace with the given number, creating it on first use
    /// bound to the given output.
    /// </summary>
    public Workspace GetOrCreate(int index, string outputName)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(index, 1);
        if (!_workspaces.TryGetValue(index, out var workspace))
        {
            workspace = new Workspace(index, outputName);
            _workspaces[index] = workspace;
        }

        return workspace;
    }

    /// <summary>Binds the workspace with the given number to the given output.</summary>
    public void Bind(int index, string outputName) =>
        GetOrCreate(index, outputName).OutputName = outputName;

    /// <summary>The workspace that is active on the output, if any was chosen yet.</summary>
    public int? GetActiveIndex(string outputName) =>
        _activeByOutput.TryGetValue(outputName, out var index) ? index : null;

    /// <summary>Makes the given workspace active on the output, creating it bound there.</summary>
    public void SetActiveOn(string outputName, int index)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(index, 1);
        GetOrCreate(index, outputName);
        _activeByOutput[outputName] = index;
    }

    /// <summary>Forgets the active workspace choice for an output, e.g. when its display is gone.</summary>
    public void ClearActive(string outputName) => _activeByOutput.Remove(outputName);

    /// <summary>
    /// The workspace shown on the output: the remembered active workspace when
    /// it is still bound there, else the lowest-numbered workspace bound to
    /// the output, else a fresh one.
    /// </summary>
    public Workspace ActiveOn(string outputName)
    {
        if (_activeByOutput.TryGetValue(outputName, out var index) &&
            _workspaces.TryGetValue(index, out var remembered) &&
            remembered.OutputName == outputName)
            return remembered;

        var bound = _workspaces.Values
            .Where(w => w.OutputName == outputName)
            .OrderBy(w => w.Index)
            .FirstOrDefault();
        if (bound is not null)
        {
            _activeByOutput[outputName] = bound.Index;
            return bound;
        }

        var fresh = GetOrCreate(NextFreeIndex(), outputName);
        _activeByOutput[outputName] = fresh.Index;
        return fresh;
    }

    /// <summary>
    /// Activates the workspace with the given number on the focused output:
    /// a workspace that is bound to another output swaps with the workspace
    /// that is active on the focused output, so every output keeps exactly
    /// one active workspace. Workspaces that do not exist yet are created on
    /// the focused output.
    /// </summary>
    public SwitchResult SwitchTo(int index, string focusedOutput)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(index, 1);
        var workspace = TryGet(index);
        var focused = ActiveOn(focusedOutput);

        if (workspace is not null &&
            !string.Equals(workspace.OutputName, focusedOutput, StringComparison.OrdinalIgnoreCase))
        {
            var otherOutput = workspace.OutputName;
            var otherPrevious = ActiveOn(otherOutput).Index;

            workspace.OutputName = focusedOutput;
            _activeByOutput[focusedOutput] = index;

            focused.OutputName = otherOutput;
            _activeByOutput[otherOutput] = focused.Index;

            return new SwitchResult(new[]
            {
                new OutputSwitch(focusedOutput, focused.Index, index),
                new OutputSwitch(otherOutput, otherPrevious, focused.Index),
            });
        }

        var output = workspace?.OutputName ?? focusedOutput;
        var previous = ActiveOn(output).Index;
        if (previous != index)
            _activeByOutput[output] = index;

        GetOrCreate(index, output);
        return new SwitchResult(new[] { new OutputSwitch(output, previous, index) });
    }

    /// <summary>
    /// Swaps the workspace that is active on the source output with the
    /// workspace that is active on the target output: each output keeps
    /// exactly one active workspace, and no workspace changes number.
    /// </summary>
    public WorkspaceMoveResult? MoveActiveWorkspaceToOutput(string sourceOutput, string targetOutput)
    {
        if (string.Equals(sourceOutput, targetOutput, StringComparison.OrdinalIgnoreCase))
            return null;

        var moved = ActiveOn(sourceOutput);
        var replacement = ActiveOn(targetOutput);

        moved.OutputName = targetOutput;
        _activeByOutput[targetOutput] = moved.Index;

        replacement.OutputName = sourceOutput;
        _activeByOutput[sourceOutput] = replacement.Index;

        return new WorkspaceMoveResult(moved.Index, sourceOutput, replacement.Index, targetOutput);
    }

    /// <summary>
    /// Moves the focused window of the source output's active workspace to
    /// the workspace with the given number, which is created on the source
    /// output when it does not exist yet.
    /// </summary>
    public WindowMoveResult? MoveFocusedTo(int index, string fromOutput)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(index, 1);

        var source = ActiveOn(fromOutput);
        if (source.FocusedWindowHandle is not { } handle)
            return null;

        var target = GetOrCreate(index, source.OutputName);
        if (target == source || !_engine.RemoveWindow(source, handle))
            return null;

        _engine.AddWindow(target, handle);
        return new WindowMoveResult(handle, target);
    }

    /// <summary>Adds a window to the workspace that is active on the output.</summary>
    public void AddWindow(long windowHandle, string outputName) =>
        _engine.AddWindow(ActiveOn(outputName), windowHandle);

    /// <summary>
    /// Adds a window to the workspace with the given number, creating it on
    /// first use, regardless of which workspace is active.
    /// </summary>
    public void AddWindowTo(int index, long windowHandle, string outputName) =>
        _engine.AddWindow(GetOrCreate(index, outputName), windowHandle);

    /// <summary>
    /// Applies a manual resize of a window in the workspace that owns it:
    /// sibling windows in the same split adapt proportionally.
    /// </summary>
    public bool ApplyUserResize(long windowHandle, ResizeAxis axis, int requestedSize) =>
        FindOwner(windowHandle) is { } workspace &&
        _engine.ApplyUserResize(workspace, windowHandle, axis, requestedSize);

    /// <summary>Removes the window from whichever workspace owns it.</summary>
    public bool RemoveWindow(long windowHandle)
    {
        foreach (var workspace in _workspaces.Values)
        {
            if (_engine.RemoveWindow(workspace, windowHandle))
                return true;
        }

        return false;
    }

    public Workspace? FindOwner(long windowHandle) =>
        _workspaces.Values.FirstOrDefault(ws => ws.WindowHandles.Contains(windowHandle));

    /// <summary>Points the owning workspace at the given window, e.g. when it gains keyboard focus.</summary>
    public bool SetFocusedWindow(long windowHandle)
    {
        var owner = FindOwner(windowHandle);
        if (owner is null)
            return false;

        owner.FocusedWindowHandle = windowHandle;
        return true;
    }

    /// <summary>
    /// Moves focus to the window <paramref name="offset"/> positions away in
    /// layout order on the output's active workspace, wrapping around.
    /// </summary>
    public bool FocusRelative(string outputName, int offset) =>
        _engine.FocusRelative(ActiveOn(outputName), offset);

    /// <summary>
    /// Moves the focused window of the output's active workspace within its
    /// parent split by <paramref name="offset"/> slots.
    /// </summary>
    public bool MoveFocused(string outputName, int offset) =>
        _engine.MoveFocused(ActiveOn(outputName), offset);

    /// <summary>
    /// Flips the orientation of the split containing the focused window of
    /// the output's active workspace.
    /// </summary>
    public void ToggleOrientation(string outputName) =>
        _engine.ToggleOrientation(ActiveOn(outputName));

    /// <summary>Computes the layout of the output's active workspace.</summary>
    public IReadOnlyList<AssignedRect> ComputeActiveLayout(
        string outputName, Rect monitor, LayoutOptions? options = null) =>
        _engine.ComputeLayout(ActiveOn(outputName), monitor, options);

    private int NextFreeIndex()
    {
        var index = 1;
        while (_workspaces.ContainsKey(index))
            index++;
        return index;
    }
}
