namespace RevitAgent.Contracts.Bridge
{
    using System;

    /// <summary>
    /// Commands <c>command_post</c> refuses, because they write or synchronize the model, release worksharing
    /// ownership, or end the session — dangerous when an agent is attached to a Revit that has the user's real
    /// model open. Shared (linked source) by the gateway (checked before forwarding, so it also protects older
    /// installed Bridges) and the Bridge itself (defense in depth). Plain C# for net48 and net8+.
    ///
    /// Add-in commands (<c>CustomCtrl_%…</c>) are never blocked here: running add-in buttons is the point of the tool.
    /// The UI layer can still click Revit's own Save button like a user would — agents are told to work on sandbox copies.
    /// </summary>
    public static class CommandSafety
    {
        // PostableCommand names (case-insensitive): prefixes and exact names.
        private static readonly string[] BlockedPostablePrefixes = { "Save", "Synchronize", "Relinquish" };
        private static readonly string[] BlockedPostableNames = { "ReloadLatest", "ExitRevit", "Close" };

        // Built-in ribbon/command ids (upper-cased, contains): save, sync, relinquish, reload latest, exit, close project.
        private static readonly string[] BlockedIdFragments =
        {
            "SAVE", "SYNC", "RELINQUISH", "RELOAD_LATEST", "RELOADLATEST", "APP_EXIT", "ID_EXIT", "EXIT_REVIT", "FILE_CLOSE", "CLOSE_PROJECT",
        };

        /// <summary>Returns a human-readable reason when <paramref name="command"/> must not be posted; null when allowed.</summary>
        public static string? BlockReason(string? command)
        {
            if (command == null)
            {
                return null;
            }

            var c = command.Trim();
            if (c.Length == 0 || c.StartsWith("CustomCtrl_", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            foreach (var name in BlockedPostableNames)
            {
                if (string.Equals(c, name, StringComparison.OrdinalIgnoreCase))
                {
                    return Describe(c);
                }
            }

            foreach (var prefix in BlockedPostablePrefixes)
            {
                if (c.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return Describe(c);
                }
            }

            var upper = c.ToUpperInvariant();
            if (upper.StartsWith("ID_", StringComparison.Ordinal) || upper.StartsWith("HID_", StringComparison.Ordinal))
            {
                foreach (var fragment in BlockedIdFragments)
                {
                    if (upper.IndexOf(fragment, StringComparison.Ordinal) >= 0)
                    {
                        return Describe(c);
                    }
                }
            }

            return null;
        }

        private static string Describe(string command) =>
            "command_post refuses '" + command + "': it would save, synchronize with central, relinquish/reload worksharing, " +
            "close the project or exit Revit — a risk to real models. Work on sandbox copies (doc_close discards changes); " +
            "saving or syncing a real model is a human decision.";
    }
}
