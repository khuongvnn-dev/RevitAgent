namespace RevitAgent.Bridge.Tools
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Reflection;
    using Autodesk.Revit.DB;
    using Autodesk.Revit.UI;
    using RevitAgent.Bridge.Runtime;
    using RevitAgent.Contracts.Bridge.Wire;
    using RevitAgent.Contracts.Protocol;

    /// <summary>Facts about this Bridge/Revit captured once at startup (safe to read from any thread).</summary>
    internal sealed class BridgeRuntimeInfo
    {
        public BridgeRuntimeInfo(string versionNumber, string versionName, string versionBuild, string subVersion, string logFile)
        {
            var process = Process.GetCurrentProcess();
            Pid = process.Id;
            ProcessStart = SafeStart(process);
            RevitVersionNumber = versionNumber;
            RevitVersionName = versionName;
            RevitVersionBuild = versionBuild;
            RevitSubVersion = subVersion;
            LogFile = logFile;
            var asm = typeof(BridgeRuntimeInfo).Assembly;
            BridgeVersion = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? asm.GetName().Version?.ToString() ?? "unknown";
            AssemblyPath = asm.Location;
#if NETFRAMEWORK
            Framework = ".NET Framework " + Environment.Version;
#else
            Framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
#endif
        }

        public int Pid { get; }

        public DateTime? ProcessStart { get; }

        public DateTime StartedUtc { get; } = DateTime.UtcNow;

        public string RevitVersionNumber { get; }

        public string RevitVersionName { get; }

        public string RevitVersionBuild { get; }

        public string RevitSubVersion { get; }

        public string BridgeVersion { get; }

        public string AssemblyPath { get; }

        public string Framework { get; }

        public string LogFile { get; }

        public WireObject RevitInfo() => new WireObject
        {
            { "version_number", RevitVersionNumber },
            { "version_name", RevitVersionName },
            { "version_build", RevitVersionBuild },
            { "sub_version", RevitSubVersion },
        };

        private static DateTime? SafeStart(Process p)
        {
            try
            {
                return p.StartTime;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>Validated access to a tool's JSON arguments. Errors are VALIDATION_FAILED with an actionable message.</summary>
    internal sealed class ToolArgs
    {
        public ToolArgs(WireObject raw) => Raw = raw ?? new WireObject();

        public WireObject Raw { get; }

        public bool Has(string name) => Raw[name] != null;

        public string? Str(string name)
        {
            var v = Raw[name];
            if (v == null)
            {
                return null;
            }

            if (v is WireObject || v is List<object?>)
            {
                throw Invalid("Argument '" + name + "' must be a string.");
            }

            var s = WireValues.AsString(v);
            return string.IsNullOrWhiteSpace(s) ? null : s!.Trim();
        }

        public string ReqStr(string name) => Str(name) ?? throw Invalid("Missing required argument '" + name + "' (string).");

        public long? Long(string name)
        {
            try
            {
                return WireValues.AsLong(Raw[name]);
            }
            catch (FormatException)
            {
                throw Invalid("Argument '" + name + "' must be an integer.");
            }
        }

        public bool Bool(string name, bool defaultValue)
        {
            try
            {
                return WireValues.AsBool(Raw[name]) ?? defaultValue;
            }
            catch (FormatException)
            {
                throw Invalid("Argument '" + name + "' must be true or false.");
            }
        }

        public int Limit(string name = "limit", int defaultValue = 100, int max = 1000)
        {
            var v = Long(name) ?? defaultValue;
            if (v <= 0)
            {
                throw Invalid("Argument '" + name + "' must be > 0.");
            }

            return (int)Math.Min(v, max);
        }

        public int Offset(string name = "offset")
        {
            var v = Long(name) ?? 0;
            if (v < 0)
            {
                throw Invalid("Argument '" + name + "' must be >= 0.");
            }

            return (int)Math.Min(v, int.MaxValue);
        }

        public List<object?> List(string name)
        {
            var v = Raw[name];
            if (v == null)
            {
                return new List<object?>();
            }

            if (v is List<object?> list)
            {
                return list;
            }

            return new List<object?> { v };
        }

        public List<long> IdList(string name)
        {
            var result = new List<long>();
            foreach (var item in List(name))
            {
                try
                {
                    var id = WireValues.AsLong(item);
                    if (id.HasValue)
                    {
                        result.Add(id.Value);
                    }
                }
                catch (FormatException)
                {
                    throw Invalid("Argument '" + name + "' must be an array of integer element ids.");
                }
            }

            return result;
        }

        public static BridgeToolException Invalid(string message) => new BridgeToolException(GatewayErrorCodes.ValidationFailed, message);
    }

    /// <summary>Everything a Revit-thread tool needs.</summary>
    internal sealed class ToolContext
    {
        public ToolContext(UIApplication app, ToolArgs args, BridgeRuntimeInfo runtime, DialogEventRecorder dialogs)
        {
            App = app;
            Args = args;
            Runtime = runtime;
            Dialogs = dialogs;
        }

        public UIApplication App { get; }

        public ToolArgs Args { get; }

        public BridgeRuntimeInfo Runtime { get; }

        public DialogEventRecorder Dialogs { get; }

        public Document? ActiveDocument => App.ActiveUIDocument?.Document;

        /// <summary>
        /// The document named by the optional <c>document</c> argument (title or full path of an open,
        /// non-linked document), else the active document. TARGET_NOT_READY when nothing is open.
        /// <paramref name="exactPath"/>: see <see cref="FindOpenDocument"/>.
        /// </summary>
        public Document Document(bool exactPath = false)
        {
            var key = Args.Str("document");
            if (key == null)
            {
                return ActiveDocument ?? throw new BridgeToolException(
                    GatewayErrorCodes.TargetNotReady,
                    "No document is open in this Revit. Open a sandbox copy with doc_open first (doc_list_open shows what is open).");
            }

            var doc = FindOpenDocument(key, exactPath);
            return doc ?? throw new BridgeToolException(
                GatewayErrorCodes.NotFound,
                "No open document matches '" + key + "'. Use doc_list_open and pass its title or path as 'document'.");
        }

        /// <summary>
        /// Open, non-linked document by full path first (its PathName, or for a detached copy without one, the file doc_open
        /// opened it from), then by title. With <paramref name="exactPath"/>, a path key never falls back to a title: two
        /// documents can share a title (a sandbox copy keeps the original's file name).
        /// </summary>
        public Document? FindOpenDocument(string key, bool exactPath = false)
        {
            string? fullKey = null;
            try
            {
                if (Path.IsPathRooted(key))
                {
                    fullKey = Path.GetFullPath(key);
                }
            }
            catch (Exception)
            {
            }

            if (fullKey != null)
            {
                foreach (Document d in App.Application.Documents)
                {
                    if (d.IsLinked)
                    {
                        continue;
                    }

                    var file = string.IsNullOrEmpty(d.PathName) ? DocumentOrigins.Of(d) : SafeFull(d.PathName);
                    if (file != null && string.Equals(file, fullKey, StringComparison.OrdinalIgnoreCase))
                    {
                        return d;
                    }
                }

                if (exactPath)
                {
                    return null;
                }
            }

            foreach (Document d in App.Application.Documents)
            {
                if (!d.IsLinked && TitleMatches(d.Title, key))
                {
                    return d;
                }
            }

            return null;
        }

        public bool IsActive(Document doc)
        {
            var active = ActiveDocument;
            return active != null && active.Equals(doc);
        }

        /// <summary>Revit's Title may or may not include the extension (Windows "hide extensions" setting).</summary>
        public static bool TitleMatches(string title, string key)
        {
            if (string.Equals(title, key, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return string.Equals(Path.GetFileNameWithoutExtension(title), Path.GetFileNameWithoutExtension(key), StringComparison.OrdinalIgnoreCase)
                && (Path.GetExtension(title).Length == 0 || Path.GetExtension(key).Length == 0);
        }

        private static string SafeFull(string p)
        {
            try
            {
                return Path.GetFullPath(p);
            }
            catch (Exception)
            {
                return p;
            }
        }
    }

    /// <summary>
    /// The file each document opened by doc_open came from. A copy detached from central has no PathName until it is
    /// saved; this lets the gateway verify such a copy came from its sandbox (doc_info reports it as <c>opened_from</c>).
    /// </summary>
    internal static class DocumentOrigins
    {
        private static readonly object Gate = new object();
        private static readonly List<KeyValuePair<Document, string>> Entries = new List<KeyValuePair<Document, string>>();

        public static void Remember(Document doc, string fullPath)
        {
            lock (Gate)
            {
                Entries.RemoveAll(e => !IsValid(e.Key) || Same(e.Key, doc));
                Entries.Add(new KeyValuePair<Document, string>(doc, fullPath));
            }
        }

        /// <summary>The file doc_open opened <paramref name="doc"/> from, only while it has no PathName of its own; else null.</summary>
        public static string? Of(Document doc)
        {
            if (!string.IsNullOrEmpty(doc.PathName))
            {
                return null;
            }

            lock (Gate)
            {
                Entries.RemoveAll(e => !IsValid(e.Key));
                foreach (var e in Entries)
                {
                    if (Same(e.Key, doc))
                    {
                        return e.Value;
                    }
                }
            }

            return null;
        }

        private static bool IsValid(Document d)
        {
            try
            {
                return d.IsValidObject;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool Same(Document a, Document b)
        {
            try
            {
                return a.Equals(b);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
