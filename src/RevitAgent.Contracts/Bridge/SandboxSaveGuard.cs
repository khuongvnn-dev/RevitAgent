namespace RevitAgent.Contracts.Bridge
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;

    /// <summary>
    /// Save guard for a Revit the gateway launched while sandbox enforcement is on. revit_launch gives the child process
    /// <see cref="LaunchedVariable"/>=1 and <see cref="SandboxRootVariable"/> (the sandbox roots, ';'-separated); the Bridge then
    /// cancels every save whose target is outside those roots, and every synchronize with central. A Revit the user started
    /// has neither variable, so its saves are never touched. Shared (linked source) by the gateway (sets the variables) and the
    /// Bridge (enforces them). Plain C# for net48 and net8+.
    /// </summary>
    public sealed class SandboxSaveGuard
    {
        public const string LaunchedVariable = "REVITAGENT_LAUNCHED";
        public const string SandboxRootVariable = "REVITAGENT_SANDBOX_ROOT";
        public const char RootSeparator = ';';

        private readonly string[] _roots;

        public SandboxSaveGuard(IEnumerable<string> roots)
        {
            _roots = roots.Select(FullDirectory).Where(r => r != null).Select(r => r!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        public IReadOnlyList<string> Roots => _roots;

        /// <summary>The value of <see cref="SandboxRootVariable"/> for <paramref name="roots"/>.</summary>
        public static string JoinRoots(IEnumerable<string> roots) => string.Join(RootSeparator.ToString(), roots);

        /// <summary>
        /// The guard of this process, or null when it was not launched by the gateway with sandbox enforcement on
        /// (no guard: the user's own Revit behaves exactly as without the Bridge).
        /// </summary>
        public static SandboxSaveGuard? FromEnvironment(Func<string, string?> getVariable)
        {
            if (getVariable(LaunchedVariable)?.Trim() != "1")
            {
                return null;
            }

            var value = getVariable(SandboxRootVariable);
            if (value == null || value.Trim().Length == 0)
            {
                return null;
            }

            // No usable root in the value: the guard still applies and every save is outside (fail closed).
            return new SandboxSaveGuard(value.Split(new[] { RootSeparator }, StringSplitOptions.RemoveEmptyEntries));
        }

        /// <summary>True when <paramref name="path"/> is a local file path inside one of the roots (UNC and cloud paths never are).</summary>
        public bool IsInside(string? path)
        {
            if (path == null || path.Trim().Length == 0)
            {
                return false;
            }

            string full;
            try
            {
                if (!Path.IsPathRooted(path))
                {
                    return false;
                }

                full = Path.GetFullPath(path);
            }
            catch (Exception)
            {
                return false; // "BIM 360://...", invalid characters: not a sandbox file
            }

            if (full.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return false;
            }

            foreach (var root in _roots)
            {
                if (full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Why a save to <paramref name="path"/> is cancelled, or null when it is allowed.</summary>
        public string? SaveRefusal(string? path) =>
            IsInside(path)
                ? null
                : "save target '" + (path ?? string.Empty) + "' is outside the gateway sandbox (" + string.Join(", ", _roots) + "); the original model is never saved over";

        private static string? FullDirectory(string root)
        {
            try
            {
                var trimmed = root.Trim().Trim('"');
                if (trimmed.Length == 0 || !Path.IsPathRooted(trimmed))
                {
                    return null;
                }

                var full = Path.GetFullPath(trimmed).TrimEnd('\\', '/');
                return full.StartsWith(@"\\", StringComparison.Ordinal) || full.Length < 3 ? null : full;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
