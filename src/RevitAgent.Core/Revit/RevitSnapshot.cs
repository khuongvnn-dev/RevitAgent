namespace RevitAgent.Core.Revit;

/// <summary>A blocker found by message-free Win32 detection (before reading its details).</summary>
public sealed record RawBlocker(WindowInfo Window, string Kind);

/// <summary>Values of the <c>state</c> field shared by revit_get_state, revit_list_instances and the readiness gate.</summary>
public static class RevitStates
{
    /// <summary>Main window present, enabled, responding, nothing blocking.</summary>
    public const string Ready = "ready";

    /// <summary>Alive but no usable main window yet (still starting, or between documents).</summary>
    public const string Busy = "busy";

    /// <summary>A modal dialog / modal window blocks the main window.</summary>
    public const string Modal = "modal";

    /// <summary>Main window "Not Responding".</summary>
    public const string NotResponding = "not_responding";

    public const string Exited = "exited";

    /// <summary>Readiness wait only: not ready before the timeout.</summary>
    public const string Timeout = "timeout";
}

/// <summary>Message-free Win32 view of one process: main window and blockers (no UIA, no WM_GETTEXT).</summary>
public sealed record RevitSnapshot(int Pid, bool Alive, string? ProcessName, WindowInfo? Main, IReadOnlyList<RawBlocker> Blockers)
{
    public bool HasMainWindow => Main is not null;

    /// <summary>Alive, main window present, enabled, responding, no blocker.</summary>
    public bool IsClear => Alive && Main is { Enabled: true, Hung: false } && Blockers.Count == 0;

    /// <summary>A dialog, modal window or disabled main window blocks input.</summary>
    public bool IsBlocked => Blockers.Any(b => b.Kind != BlockerKinds.Hung) || Main is { Enabled: false };

    /// <summary>ready | busy | modal | not_responding | exited (see <see cref="RevitStates"/>).</summary>
    public string State =>
        !Alive ? RevitStates.Exited :
        Blockers.Any(b => b.Kind == BlockerKinds.Hung) ? RevitStates.NotResponding :
        IsBlocked ? RevitStates.Modal :
        Main is null ? RevitStates.Busy :
        RevitStates.Ready;
}
