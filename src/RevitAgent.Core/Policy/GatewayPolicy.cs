using RevitAgent.Core.RunStore;
using RevitAgent.Core.Tools;

namespace RevitAgent.Core.Policy;

/// <summary>
/// Gateway-side guard: enforced at the gateway, so it holds for every
/// MCP client, with or without client-side hooks. Mutable at runtime from the dashboard.
/// </summary>
public sealed class GatewayPolicy
{
    private readonly HashSet<string> _deniedTools = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _allowedInputProcesses = new();
    private readonly object _gate = new();

    /// <summary>Highest permission a tool may have to run. Default: everything (the user's own tool).</summary>
    public ToolPermission MaxPermission { get; set; } = ToolPermission.Admin;

    /// <summary>When true, Write tools may only target documents/files inside the sandbox (runs folder).</summary>
    public bool EnforceSandbox { get; set; } = true;

    /// <summary>Extra folders (besides the runs folder) treated as sandbox, e.g. a team suites_dir.</summary>
    public List<string> ExtraSandboxRoots { get; } = new();

    /// <summary>
    /// Process names (without .exe) besides Revit that ui_* input may reach, set by the owner. Default empty: input goes to
    /// Revit only. The gateway's own processes are never reachable, whatever this list holds (<see cref="InputTargetGuard"/>).
    /// </summary>
    public IReadOnlyList<string> AllowedInputProcesses
    {
        get
        {
            lock (_gate)
            {
                return _allowedInputProcesses.ToArray();
            }
        }
    }

    /// <summary>Replaces <see cref="AllowedInputProcesses"/> (dashboard settings; tests add their helper processes).</summary>
    public void SetAllowedInputProcesses(IEnumerable<string>? names)
    {
        var clean = (names ?? Array.Empty<string>()).Select(StripExe).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        lock (_gate)
        {
            _allowedInputProcesses.Clear();
            _allowedInputProcesses.AddRange(clean);
        }
    }

    /// <summary>True when <paramref name="processName"/> is on <see cref="AllowedInputProcesses"/> (".exe" and case ignored).</summary>
    public bool IsInputProcessAllowed(string? processName)
    {
        var name = StripExe(processName);
        lock (_gate)
        {
            return name.Length > 0 && _allowedInputProcesses.Contains(name, StringComparer.OrdinalIgnoreCase);
        }
    }

    public void Deny(string toolName)
    {
        lock (_gate)
        {
            _deniedTools.Add(toolName);
        }
    }

    public void Allow(string toolName)
    {
        lock (_gate)
        {
            _deniedTools.Remove(toolName);
        }
    }

    /// <summary>Throws PERMISSION_DENIED when the tool may not run under the current policy.</summary>
    public void EnsureAllowed(IGatewayTool tool)
    {
        lock (_gate)
        {
            if (_deniedTools.Contains(tool.Name))
            {
                throw GatewayException.Denied($"Tool '{tool.Name}' is disabled by the gateway policy (dashboard → Settings).");
            }
        }

        if (tool.Permission > MaxPermission)
        {
            throw GatewayException.Denied(
                $"Tool '{tool.Name}' needs permission '{tool.Permission}', but the gateway currently allows up to '{MaxPermission}'. Ask the user to raise it in the dashboard.");
        }
    }

    /// <summary>True when <paramref name="path"/> is inside the runs folder or an extra sandbox root.</summary>
    public bool IsInSandbox(string? path, AgentPaths paths)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception)
        {
            return false;
        }

        var roots = SandboxRoots(paths);
        if (full.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return false; // UNC / device paths are never sandbox paths.
        }

        foreach (var root in roots)
        {
            var rootFull = Path.GetFullPath(root).TrimEnd('\\', '/');
            var r = rootFull + Path.DirectorySeparatorChar;
            if (full.StartsWith(r, StringComparison.OrdinalIgnoreCase))
            {
                // Lexical containment is not enough: a junction / symlink inside the sandbox could point at an
                // original model (review 2026-11-20). Reject any existing reparse point from the root to the leaf.
                return !HasReparsePoint(rootFull, full);
            }
        }

        return false;
    }

    /// <summary>Every sandbox root (full paths): extra roots, the configured run-data root and the runs folder.</summary>
    public IReadOnlyList<string> SandboxRoots(AgentPaths paths)
    {
        lock (_gate)
        {
            var configuredRoot = RunStoreOptions.Load(paths).GetEffectiveRoot(paths);
            return ExtraSandboxRoots
                .Append(configuredRoot)
                .Append(paths.RunsDir)
                .Select(r => Path.GetFullPath(r).TrimEnd('\\', '/'))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    private static string StripExe(string? name)
    {
        var n = (name ?? string.Empty).Trim();
        return n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n[..^4].Trim() : n;
    }

    private static bool HasReparsePoint(string rootFull, string full)
    {
        try
        {
            for (var current = full; current is not null && current.Length >= rootFull.Length; current = Path.GetDirectoryName(current))
            {
                if (File.Exists(current) || Directory.Exists(current))
                {
                    if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                    {
                        return true;
                    }
                }
            }

            return false;
        }
        catch (Exception)
        {
            return true; // Fail closed when attributes cannot be read.
        }
    }

    /// <summary>Throws SANDBOX_VIOLATION when sandbox enforcement is on and the path is outside.</summary>
    public void EnsureInSandbox(string? path, AgentPaths paths, string what)
    {
        if (EnforceSandbox && !IsInSandbox(path, paths))
        {
            var configuredRoot = RunStoreOptions.Load(paths).GetEffectiveRoot(paths);
            throw new GatewayException(
                Contracts.Protocol.GatewayErrorCodes.SandboxViolation,
                $"{what} '{path}' is outside the gateway sandbox ({configuredRoot}). Copy the model into a run folder first (sandbox_copy_model) and use the copy.");
        }
    }
}
