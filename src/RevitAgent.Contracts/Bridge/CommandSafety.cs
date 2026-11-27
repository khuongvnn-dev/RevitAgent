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
            if (c.Length == 0 || IsAddInCommand(c))
            {
                return null;
            }

            // Enum.TryParse (older Bridges) reads "Wall,Save" as Wall|Save and "+12" as the value 12: either can resolve
            // to a blocked PostableCommand whose name never appears in the text. One plain name or id only.
            if (IsMalformed(c))
            {
                return "command_post refuses '" + command + "': pass exactly one PostableCommand name or command id " +
                       "(no commas, spaces, signs or numeric values), e.g. Wall or ID_OBJECTS_WALL.";
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
            if ((upper.StartsWith("ID_", StringComparison.Ordinal) || upper.StartsWith("HID_", StringComparison.Ordinal)) && HasBlockedFragment(upper))
            {
                return Describe(c);
            }

            return null;
        }

        /// <summary>
        /// The check for the command id Revit actually resolved (<c>RevitCommandId.Name</c>, e.g. <c>ID_FILE_SAVE</c>): the
        /// id fragments apply whatever the id looks like, so a name that maps to a save / sync / close / exit id is refused
        /// even when the text the agent passed looked harmless. Add-in ids (<c>CustomCtrl_</c>) stay allowed.
        /// </summary>
        public static string? ResolvedIdBlockReason(string? commandId)
        {
            if (commandId == null)
            {
                return null;
            }

            var c = commandId.Trim();
            if (c.Length == 0 || IsAddInCommand(c))
            {
                return null;
            }

            return HasBlockedFragment(c.ToUpperInvariant()) ? Describe(c) : BlockReason(c);
        }

        private static bool IsAddInCommand(string c) => c.StartsWith("CustomCtrl_", StringComparison.OrdinalIgnoreCase);

        /// <summary>A comma list, inner whitespace, a leading sign or a plain number: never one command name or id.</summary>
        private static bool IsMalformed(string c)
        {
            if (c[0] == '+' || c[0] == '-')
            {
                return true;
            }

            var allDigits = true;
            foreach (var ch in c)
            {
                if (ch == ',' || char.IsWhiteSpace(ch))
                {
                    return true;
                }

                allDigits &= char.IsDigit(ch);
            }

            return allDigits;
        }

        private static bool HasBlockedFragment(string upper)
        {
            foreach (var fragment in BlockedIdFragments)
            {
                if (upper.IndexOf(fragment, StringComparison.Ordinal) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static string Describe(string command) =>
            "command_post refuses '" + command + "': it would save, synchronize with central, relinquish/reload worksharing, " +
            "close the project or exit Revit — a risk to real models. Work on sandbox copies (doc_close discards changes); " +
            "saving or syncing a real model is a human decision.";
    }
}
