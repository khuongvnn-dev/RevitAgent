using System.Diagnostics;
using RevitAgent.Contracts.Protocol;
using RevitAgent.Core.Revit.Native;
using RevitAgent.Core.Tools;

namespace RevitAgent.Core.Revit;

/// <summary>One running Revit.exe (revit_list_instances).</summary>
public sealed record RevitInstance
{
    public int Pid { get; init; }

    public int? Year { get; init; }

    public string? ExePath { get; init; }

    public string? MainWindowHwnd { get; init; }

    public string? MainWindowTitle { get; init; }

    /// <summary>From IsHungAppWindow (no message is sent); null without a main window.</summary>
    public bool? Responding { get; init; }

    public bool? MainWindowEnabled { get; init; }

    public int ModalDialogCount { get; init; }

    /// <summary>ready | busy | modal | not_responding (message-free detection).</summary>
    public string State { get; init; } = RevitStates.Ready;

    public bool LaunchedByGateway { get; init; }

    public bool? BridgePipeAvailable { get; init; }

    public DateTime? StartTime { get; init; }

    /// <summary>Document title guessed from the main window title (e.g. "Project1"); verify with doc_info.</summary>
    public string? ActiveDocumentGuess { get; init; }

    public string? ActiveViewGuess { get; init; }
}

public sealed record LaunchRequest
{
    public int? Year { get; init; }

    public string? ExePath { get; init; }

    public IReadOnlyList<string> Args { get; init; } = Array.Empty<string>();

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(300);

    public TimeSpan Settle { get; init; } = TimeSpan.FromSeconds(5);

    public bool TieToGateway { get; init; }

    /// <summary>Unattended: forbidden dialogs of this launched Revit are put to the owner on Telegram (see ReadinessOptions.EscalateForbiddenDialogs).</summary>
    public bool EscalateDialogs { get; init; }

    /// <summary>Null: wait for the Bridge pipe only when the Bridge manifest is installed for that Revit year.</summary>
    public bool? WaitForBridge { get; init; }
}

public sealed record LaunchResult
{
    public int Pid { get; init; }

    public int Year { get; init; }

    public string ExePath { get; init; } = string.Empty;

    public bool Ready { get; init; }

    /// <summary>ready | modal | not_responding | timeout | exited.</summary>
    public string State { get; init; } = RevitStates.Ready;

    /// <summary>MODAL_BLOCKED / TARGET_NOT_READY / NOT_FOUND when not ready.</summary>
    public string? Code { get; init; }

    public string? Reason { get; init; }

    public string? NextStep { get; init; }

    public IReadOnlyList<HandledDialog> Handled { get; init; } = Array.Empty<HandledDialog>();

    public IReadOnlyList<Blocker> Blockers { get; init; } = Array.Empty<Blocker>();

    public IReadOnlyList<string> Screenshots { get; init; } = Array.Empty<string>();

    public double WaitedSec { get; init; }

    public bool BridgeManifestInstalled { get; init; }

    public bool? BridgePipeAvailable { get; init; }

    public bool TiedToGateway { get; init; }

    public IReadOnlyList<string>? Warnings { get; init; }
}

public sealed record CloseRequest
{
    public int Pid { get; init; }

    public bool DiscardChanges { get; init; }

    public TimeSpan Grace { get; init; } = TimeSpan.FromSeconds(60);

    public bool ForceForeign { get; init; }

    public int? ConfirmPid { get; init; }

    public bool ForceKill { get; init; }
}

public sealed record CloseResult
{
    public int Pid { get; init; }

    public bool Closed { get; init; }

    /// <summary>graceful | killed | killed_after_grace | already_exited.</summary>
    public string Method { get; init; } = "graceful";

    public string? Note { get; init; }

    public IReadOnlyList<HandledDialog> Handled { get; init; } = Array.Empty<HandledDialog>();

    public double WaitedSec { get; init; }

    public bool LaunchedByGateway { get; init; }
}

/// <summary>Lists, launches and closes Revit processes. Closing is limited to processes the gateway launched.</summary>
public sealed class RevitProcessService : IDisposable
{
    public const string ProcessName = "Revit";
    public const string BridgeManifestName = "RevitAgent.Bridge.addin";

    private static readonly string[] ForbiddenArgExtensions = { ".rvt", ".rfa", ".rte", ".rft", ".rvtx", ".rws", ".adsk", ".ifc", ".dwg", ".txt", ".jrn" };

    private readonly LaunchedProcessRegistry _launched;
    private readonly RevitReadiness _readiness;
    private readonly DialogRuleProvider _rules;
    private readonly GatewayEvents _events;
    private readonly object _jobGate = new();
    private KillOnCloseJob? _job;

    public RevitProcessService(LaunchedProcessRegistry launched, RevitReadiness readiness, DialogRuleProvider rules, GatewayEvents events)
    {
        _launched = launched;
        _readiness = readiness;
        _rules = rules;
        _events = events;
    }

    /// <summary>Every Revit.exe process. Message-free: nothing is sent to the processes.</summary>
    public IReadOnlyList<RevitInstance> ListInstances()
    {
        var pipes = BridgePipes.ListPids();
        var owned = _launched.List().Select(e => e.Pid).ToHashSet();
        var result = new List<RevitInstance>();
        foreach (var p in Process.GetProcessesByName(ProcessName))
        {
            using (p)
            {
                var pid = p.Id;
                var exe = Win32Windows.ProcessImagePath(pid);
                var snap = _readiness.Detect(pid);
                if (!snap.Alive)
                {
                    continue;
                }

                var (doc, view) = GuessDocument(snap.Main?.Title);
                result.Add(new RevitInstance
                {
                    Pid = pid,
                    Year = exe is null ? null : RevitInstallations.YearOfExecutable(exe),
                    ExePath = exe,
                    MainWindowHwnd = snap.Main?.HwndHex,
                    MainWindowTitle = snap.Main?.Title,
                    Responding = snap.Main is null ? null : !snap.Main.Hung,
                    MainWindowEnabled = snap.Main?.Enabled,
                    ModalDialogCount = snap.Blockers.Count(b => b.Kind is BlockerKinds.Dialog or BlockerKinds.ModalWindow),
                    State = snap.State,
                    LaunchedByGateway = owned.Contains(pid),
                    BridgePipeAvailable = pipes?.Contains(pid),
                    StartTime = Win32Windows.ProcessStartUtc(p),
                    ActiveDocumentGuess = doc,
                    ActiveViewGuess = view,
                });
            }
        }

        return result.OrderBy(i => i.Pid).ToArray();
    }

    /// <summary>Starts Revit without a model, records ownership, then runs the start-phase readiness gate.</summary>
    public async Task<LaunchResult> LaunchAsync(LaunchRequest request, CancellationToken cancellationToken)
    {
        ValidateArgs(request.Args);
        var install = ResolveInstallation(request.Year, request.ExePath);
        var manifest = File.Exists(Path.Combine(install.UserAddinsDir, BridgeManifestName)) ||
                       File.Exists(Path.Combine(install.MachineAddinsDir, BridgeManifestName));
        var waitBridge = request.WaitForBridge ?? manifest;

        // UseShellExecute = true: CreateProcess with bInheritHandles = FALSE. With UseShellExecute = false .NET hands
        // Revit every inheritable handle of this process — in a headless Shim those are the MCP client's stdio pipes, so
        // the client never saw EOF and its call "hung" until Revit exited (2026-11-25).
        var psi = new ProcessStartInfo(install.ExePath) { UseShellExecute = true, WorkingDirectory = install.InstallDir };
        foreach (var a in request.Args)
        {
            psi.ArgumentList.Add(a);
        }

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw GatewayException.Dependency($"Revit {install.Year} did not start.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw GatewayException.Dependency($"Revit {install.Year} could not be started ({ex.Message}).");
        }

        var warnings = new List<string>();
        var sw = Stopwatch.StartNew();
        using (process)
        {
            try
            {
                _launched.Register(process, install.Year, request.TieToGateway);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                // Never leave a Revit running that the gateway cannot prove it owns (it could not close it later).
                Kill(process);
                throw GatewayException.Dependency($"Revit {install.Year} was started but its ownership could not be recorded ({ex.GetType().Name}); it was stopped again.");
            }

            var tied = false;
            if (request.TieToGateway)
            {
                tied = TieToGateway(process, out var err);
                if (!tied)
                {
                    warnings.Add($"Could not tie pid {process.Id} to the gateway (Win32 error {err}); Revit will outlive the gateway.");
                }
            }

            _events.RaiseStatus("revit", $"Revit {install.Year} (pid {process.Id}) launched by the gateway");
            var pid = process.Id;
            var options = new ReadinessOptions
            {
                Phase = ReadinessPhase.Start,
                Timeout = request.Timeout,
                Settle = request.Settle,
                ApplyRules = true,
                RequireBridgePipe = waitBridge,
                EscalateForbiddenDialogs = request.EscalateDialogs,
                SafeDismissFor = "revit_launch",
            };
            var gate = await _readiness.WaitReadyAsync(pid, options, cancellationToken).ConfigureAwait(false);

            var nextStep = gate.Ready
                ? "Revit is ready. Copy the model into the sandbox (sandbox_copy_model) and open the copy with doc_open."
                : gate.NextStep;
            if (!gate.Ready && gate.State != RevitStates.Exited)
            {
                nextStep += $" Revit (pid {pid}) is left running; the gateway does not kill it automatically — close it with revit_close if needed.";
            }

            return new LaunchResult
            {
                Pid = pid,
                Year = install.Year,
                ExePath = install.ExePath,
                Ready = gate.Ready,
                State = gate.State,
                Code = gate.Code,
                Reason = gate.Reason,
                NextStep = nextStep,
                Handled = gate.Handled,
                Blockers = gate.Blockers,
                Screenshots = gate.Screenshots,
                WaitedSec = Math.Round(sw.Elapsed.TotalSeconds, 1),
                BridgeManifestInstalled = manifest,
                BridgePipeAvailable = BridgePipes.IsAvailable(pid),
                TiedToGateway = tied,
                Warnings = warnings.Count > 0 ? warnings : null,
            };
        }
    }

    /// <summary>
    /// Closes a Revit the gateway launched (or a foreign one with explicit confirmation): WM_CLOSE to the main window,
    /// answers a save prompt with "don't save" only when <see cref="CloseRequest.DiscardChanges"/>, kills after the grace period.
    /// In a Revit the gateway launched, a harmless dialog in the way gets its Close / Later / Not now button pressed
    /// (<see cref="RevitReadiness.TrySafeDismiss"/>; never Cancel, which would cancel the close).
    /// </summary>
    public async Task<CloseResult> CloseAsync(CloseRequest request, CancellationToken cancellationToken)
    {
        var pid = request.Pid;
        using var process = TryGet(pid) ?? throw GatewayException.NotFound($"No process with pid {pid}. Call revit_list_instances.");
        var owned = _launched.IsOwned(pid);
        if (!owned)
        {
            if (!request.ForceForeign)
            {
                throw GatewayException.Denied(
                    $"pid {pid} was not launched by the gateway — it may be the user's own Revit with unsaved work. The gateway only closes Revit processes it started (revit_launch). " +
                    $"Only if the user explicitly asked to close this Revit, call again with force_foreign:true and confirm_pid:{pid}.");
            }

            if (request.ConfirmPid != pid)
            {
                throw GatewayException.Denied($"force_foreign requires confirm_pid to equal pid ({pid}). Ask the user to confirm which Revit to close.");
            }

            if (!string.Equals(SafeName(process), ProcessName, StringComparison.OrdinalIgnoreCase))
            {
                throw GatewayException.Validation($"pid {pid} is not a Revit.exe process; revit_close only closes Revit.");
            }
        }

        using var actionLock = _readiness.AcquireActionLock(pid);
        if (owned)
        {
            _launched.BeginGracefulClose(pid);
        }
        try
        {
            return await CloseCoreAsync(request, process, pid, owned, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (owned && !HasExited(process))
            {
                _launched.CancelGracefulClose(pid);
            }
            throw;
        }
    }

    private async Task<CloseResult> CloseCoreAsync(CloseRequest request, Process process, int pid, bool owned, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var handled = new List<HandledDialog>();
        var dismissAttempts = new Dictionary<IntPtr, int>();
        if (HasExited(process))
        {
            _launched.Remove(pid);
            return Done(pid, "already_exited", "The process had already exited.", handled, sw, owned);
        }

        if (request.ForceKill)
        {
            Kill(process);
            return Done(pid, "killed", "Killed immediately (force_kill); unsaved changes were lost.", handled, sw, owned);
        }

        // A dialog that is already open would swallow WM_CLOSE: only a save prompt (with discard_changes) is handled.
        // Answering a prompt left by an earlier close request lets that close finish on its own.
        var snap = _readiness.Detect(pid);
        AnswerSavePromptsOrThrow(snap, request.DiscardChanges, owned, handled, dismissAttempts, closing: false);
        if (handled.Count > 0 && process.WaitForExit(5000))
        {
            return Exited(pid, handled, sw, owned);
        }

        // WM_CLOSE goes to the main window as soon as there is one (it may be missing briefly while Revit loads).
        var posted = false;
        while (sw.Elapsed < request.Grace)
        {
            cancellationToken.ThrowIfCancellationRequested();
            snap = _readiness.Detect(pid);
            if (!snap.Alive || HasExited(process))
            {
                return Exited(pid, handled, sw, owned);
            }

            if (!posted && snap.Main is not null)
            {
                NativeMethods.PostMessage(snap.Main.Hwnd, NativeMethods.WmClose, IntPtr.Zero, IntPtr.Zero);
                posted = true;
            }

            AnswerSavePromptsOrThrow(snap, request.DiscardChanges, owned, handled, dismissAttempts, closing: posted);
            if (process.WaitForExit(400))
            {
                return Exited(pid, handled, sw, owned);
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        Kill(process);
        return Done(
            pid,
            "killed_after_grace",
            posted
                ? $"Revit did not exit within {request.Grace.TotalSeconds:0} s of WM_CLOSE (no save prompt appeared) and was killed."
                : $"No main window appeared within {request.Grace.TotalSeconds:0} s to close gracefully; the process was killed.",
            handled,
            sw,
            owned);
    }

    /// <summary>Title "Autodesk Revit 2024.2 - [Project1 - Floor Plan: Level 1]" → ("Project1", "Floor Plan: Level 1").</summary>
    public static (string? Document, string? View) GuessDocument(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return (null, null);
        }

        var idx = title.IndexOf(" - ", StringComparison.Ordinal);
        if (idx < 0)
        {
            return (null, null);
        }

        var rest = title[(idx + 3)..].Trim().TrimStart('[').TrimEnd(']').Trim();
        if (rest.Length == 0)
        {
            return (null, null);
        }

        var parts = rest.Split(" - ", 2, StringSplitOptions.TrimEntries);
        return (parts[0].Length > 0 ? parts[0] : null, parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null);
    }

    /// <summary>Rejects launch arguments that would open a model or replay a journal (agents open sandbox copies with doc_open).</summary>
    public static void ValidateArgs(IReadOnlyList<string> args)
    {
        foreach (var a in args)
        {
            var s = (a ?? string.Empty).Trim().Trim('"');
            string ext;
            bool existing;
            try
            {
                ext = Path.GetExtension(s);
                existing = s.Length > 0 && (File.Exists(s) || Directory.Exists(s));
            }
            catch (ArgumentException)
            {
                throw GatewayException.Validation($"Launch argument '{a}' is not valid.");
            }

            if (ForbiddenArgExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase) || existing)
            {
                throw GatewayException.Validation(
                    $"Launch argument '{a}' names a file or folder. revit_launch starts Revit without a model: copy the model with sandbox_copy_model and open the copy with doc_open. Journal files are not accepted; only switches such as /language ENU are.");
            }
        }
    }

    public static RevitInstallation ResolveInstallation(int? year, string? exePath)
    {
        var installs = RevitInstallations.Discover();
        var known = installs.Count == 0 ? "none found" : string.Join(", ", installs.Select(i => $"{i.Year} ({i.ExePath})"));
        if (!string.IsNullOrWhiteSpace(exePath))
        {
            string full;
            try
            {
                full = Path.GetFullPath(exePath);
            }
            catch (Exception)
            {
                throw GatewayException.Validation($"exe_path '{exePath}' is not a valid path.");
            }

            var match = installs.FirstOrDefault(i => string.Equals(Path.GetFullPath(i.ExePath), full, StringComparison.OrdinalIgnoreCase))
                ?? throw GatewayException.Validation($"exe_path must be the Revit.exe of an installed Revit (revit_list_installed). Installed: {known}.");
            if (year is not null && year != match.Year)
            {
                throw GatewayException.Validation($"exe_path is Revit {match.Year} but year {year} was requested.");
            }

            return match;
        }

        if (year is null)
        {
            throw GatewayException.Validation($"Pass 'year' (e.g. 2024) or 'exe_path'. Installed: {known}.");
        }

        if (year is < RevitInstallations.MinYear or > RevitInstallations.MaxYear)
        {
            throw GatewayException.Validation($"year must be between {RevitInstallations.MinYear} and {RevitInstallations.MaxYear}.");
        }

        return installs.FirstOrDefault(i => i.Year == year)
            ?? throw GatewayException.NotFound($"Revit {year} is not installed. Installed: {known}.");
    }

    public void Dispose()
    {
        lock (_jobGate)
        {
            // Closing the job kills tied processes — the documented meaning of tie_to_gateway.
            _job?.Dispose();
            _job = null;
        }
    }

    private void AnswerSavePromptsOrThrow(RevitSnapshot snap, bool discard, bool owned, List<HandledDialog> handled, Dictionary<IntPtr, int> dismissAttempts, bool closing)
    {
        var rules = _rules.Load();
        foreach (var raw in snap.Blockers.Where(b => b.Kind is BlockerKinds.Dialog or BlockerKinds.ModalWindow))
        {
            if (!Win32Windows.Exists(raw.Window.Hwnd))
            {
                continue;
            }

            var blocker = _readiness.Describe(raw, snap.ProcessName, rules, includeDetails: true, out var details);
            var verdict = details is null ? DialogSafety.Classify(raw.Window.Title, null) : DialogSafety.ClassifyDialog(raw.Window.Title, details);
            if (verdict.IsSavePrompt && discard && !owned)
            {
                throw new GatewayException(
                    GatewayErrorCodes.ModalBlocked,
                    $"Revit (pid {snap.Pid}) asks to save changes ('{raw.Window.Title}'). This Revit was not launched by the gateway, so discard_changes is not honored: the user must answer it.",
                    new { pid = snap.Pid, blocker, handled });
            }

            // Only a fully read dialog may use the discard exception: partial text could hide a forbidden word (review 2026-11-20).
            if (verdict.DiscardExceptionAllowed && discard && details is { Complete: true })
            {
                var dontSave = details.Buttons.Where(DialogSafety.IsDontSaveButton).ToList();
                if (dontSave.Count == 1)
                {
                    var click = _readiness.HandleDialog(snap.Pid, raw.Window.Hwnd, dontSave[0].Id ?? dontSave[0].Text, discardChanges: true, TimeSpan.FromSeconds(5), "revit_close(discard_changes)");
                    handled.Add(click.Handled);
                    continue;
                }
            }

            // A harmless dialog in the way of the close of a Revit this gateway launched: press its Close / Later / Not now
            // (SafeDismissPolicy; never Cancel here). Twice per window at most, then MODAL_BLOCKED as before.
            var hwnd = raw.Window.Hwnd;
            var attempt = dismissAttempts.GetValueOrDefault(hwnd) + 1;
            if (owned && !verdict.IsForbidden && attempt <= 2)
            {
                dismissAttempts[hwnd] = attempt;
                if (_readiness.TrySafeDismiss(snap.Pid, raw, "revit_close", details, allowCancel: false) is { } dismissed)
                {
                    handled.Add(dismissed);
                    continue;
                }

                if (closing && attempt == 1)
                {
                    continue; // it may have just appeared and not be fully readable yet: look once more on the next pass
                }
            }

            var message = verdict.IsSavePrompt
                ? discard && !verdict.DiscardExceptionAllowed
                    ? $"Revit (pid {snap.Pid}) shows '{raw.Window.Title}', a save prompt that also matches a forbidden category ({verdict.ForbiddenTerm}); it was not answered. Ask the user."
                    : $"Revit (pid {snap.Pid}) asks to save changes ('{raw.Window.Title}'); it was not answered and Revit is still open. " +
                      "If the open document is a sandbox copy (or the user agreed to lose its changes), call revit_close again with discard_changes:true; otherwise ask the user."
                : $"Dialog '{raw.Window.Title}' {(closing ? "appeared while closing" : "is open")} in Revit (pid {snap.Pid}), so it cannot close. The gateway does not answer it automatically: inspect it with revit_list_dialogs, " +
                  "answer it with revit_handle_dialog if it is informational (or ask the user), then call revit_close again; force_kill:true kills Revit and loses unsaved changes.";
            throw new GatewayException(GatewayErrorCodes.ModalBlocked, message, new { pid = snap.Pid, blocker, handled });
        }
    }

    private bool TieToGateway(Process process, out int win32Error)
    {
        lock (_jobGate)
        {
            _job ??= new KillOnCloseJob();
            return _job.TryAssign(process, out win32Error);
        }
    }

    private void Kill(Process process)
    {
        var pid = process.Id;
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or AggregateException or NotSupportedException)
        {
            // Child processes may refuse; what matters is the Revit process itself.
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                }
            }
            catch (Exception inner) when (inner is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                if (!HasExited(process))
                {
                    throw GatewayException.Dependency($"Could not kill pid {pid} ({inner.Message}).");
                }
            }
        }

        process.WaitForExit(15000);

        _launched.Remove(pid);
        _events.RaiseStatus("revit", $"Revit (pid {pid}) killed by the gateway");
    }

    private CloseResult Exited(int pid, List<HandledDialog> handled, Stopwatch sw, bool owned)
    {
        _launched.Remove(pid);
        _events.RaiseStatus("revit", $"Revit (pid {pid}) closed by the gateway");
        return Done(pid, "graceful", null, handled, sw, owned);
    }

    private static CloseResult Done(int pid, string method, string? note, List<HandledDialog> handled, Stopwatch sw, bool owned) => new()
    {
        Pid = pid,
        Closed = true,
        Method = method,
        Note = note,
        Handled = handled.ToArray(),
        WaitedSec = Math.Round(sw.Elapsed.TotalSeconds, 1),
        LaunchedByGateway = owned,
    };

    private static Process? TryGet(int pid)
    {
        try
        {
            return Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static bool HasExited(Process p)
    {
        try
        {
            return p.HasExited;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string SafeName(Process p)
    {
        try
        {
            return p.ProcessName;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }
}
