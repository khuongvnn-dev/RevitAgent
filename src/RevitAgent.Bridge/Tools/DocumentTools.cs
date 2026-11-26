namespace RevitAgent.Bridge.Tools
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using Autodesk.Revit.DB;
    using Autodesk.Revit.UI;
    using RevitAgent.Contracts.Bridge.Wire;
    using RevitAgent.Contracts.Protocol;

    /// <summary>doc_info, doc_list_open, doc_open, doc_close.</summary>
    internal static class DocumentTools
    {
        public static object? Info(ToolContext ctx)
        {
            var doc = ctx.Document();
            var isActive = ctx.IsActive(doc);
            var o = new WireObject
            {
                { "title", doc.Title },
                { "path", doc.PathName },
                { "is_active", isActive },
                { "is_family", doc.IsFamilyDocument },
                { "is_workshared", doc.IsWorkshared },
                { "is_detached", doc.IsDetached },
                { "is_modifiable", doc.IsModifiable },
                { "is_modified", doc.IsModified },
                { "is_read_only", doc.IsReadOnly },
            };

            var origin = DocumentOrigins.Of(doc);
            if (origin != null)
            {
                o["opened_from"] = origin; // detached copy (no path): the file doc_open opened it from
            }

            if (doc.IsWorkshared)
            {
                try
                {
                    var central = doc.GetWorksharingCentralModelPath();
                    o["central_path"] = central == null ? null : ModelPathUtils.ConvertModelPathToUserVisiblePath(central);
                }
                catch (Exception)
                {
                }
            }

            var uidoc = ctx.App.ActiveUIDocument;
            if (isActive && uidoc?.ActiveView is View view)
            {
                o["active_view"] = Describe.View(view);
            }

            try
            {
                var info = doc.ProjectInformation;
                if (info != null)
                {
                    o["project"] = new WireObject { { "name", info.Name }, { "number", info.Number } };
                }
            }
            catch (Exception)
            {
            }

            o["units"] = Units(doc);
            o["revit"] = ctx.Runtime.RevitInfo();
            o["open_document_count"] = CountOpen(ctx.App);
            return o;
        }

        public static object? ListOpen(ToolContext ctx)
        {
            var list = new List<object?>();
            foreach (Document d in ctx.App.Application.Documents)
            {
                list.Add(new WireObject
                {
                    { "title", d.Title },
                    { "path", d.PathName },
                    { "is_active", ctx.IsActive(d) },
                    { "is_linked", d.IsLinked },
                    { "is_family", d.IsFamilyDocument },
                    { "is_workshared", d.IsWorkshared },
                    { "is_detached", d.IsDetached },
                    { "is_modified", d.IsModified },
                });
            }

            return new WireObject { { "count", list.Count }, { "documents", list } };
        }

        public static object? Open(ToolContext ctx)
        {
            var args = ctx.Args;
            var path = args.ReqStr("path");
            if (!Path.IsPathRooted(path))
            {
                throw ToolArgs.Invalid("'path' must be an absolute file path (got '" + path + "').");
            }

            var full = Path.GetFullPath(path);
            var ext = Path.GetExtension(full).ToLowerInvariant();
            if (ext != ".rvt" && ext != ".rfa")
            {
                throw ToolArgs.Invalid("doc_open opens .rvt projects and .rfa families only (got '" + ext + "').");
            }

            if (!File.Exists(full))
            {
                throw new BridgeToolException(GatewayErrorCodes.NotFound, "File not found: " + full);
            }

            var detach = args.Bool("detach_from_central", true);
            var audit = args.Bool("audit", false);
            var activate = args.Bool("activate", true);
            var worksets = (args.Str("worksets") ?? "last_viewed").ToLowerInvariant();
            if (worksets != "last_viewed" && worksets != "open_all" && worksets != "close_all")
            {
                throw ToolArgs.Invalid("'worksets' must be last_viewed, open_all or close_all.");
            }

            var app = ctx.App;
            foreach (Document open in app.Application.Documents)
            {
                if (!open.IsLinked && !string.IsNullOrEmpty(open.PathName) &&
                    string.Equals(TryFullPath(open.PathName), full, StringComparison.OrdinalIgnoreCase))
                {
                    var wasActive = ctx.IsActive(open);
                    if (activate && !wasActive)
                    {
                        app.OpenAndActivateDocument(full);
                    }

                    return new WireObject
                    {
                        { "opened", false },
                        { "already_open", true },
                        { "title", open.Title },
                        { "path", open.PathName },
                        { "is_active", ctx.IsActive(open) },
                    };
                }
            }

            bool isWorkshared = false;
            bool headerRead = false;
            string? savedIn = null;
            try
            {
                var info = BasicFileInfo.Extract(full);
                isWorkshared = info.IsWorkshared;
                headerRead = true;
                savedIn = info.Format;
                if (info.IsSavedInLaterVersion)
                {
                    throw ToolArgs.Invalid("'" + Path.GetFileName(full) + "' was saved by Revit " + savedIn + ", newer than this Revit " + ctx.Runtime.RevitVersionNumber + ". Open it in that Revit version.");
                }
            }
            catch (BridgeToolException)
            {
                throw;
            }
            catch (Exception)
            {
                // BasicFileInfo is best effort; OpenDocumentFile reports real problems.
            }

            if (!headerRead && detach)
            {
                // Without the header we cannot tell whether this is a workshared central/local; opening it in place
                // would take worksharing locks on a real model. Refuse instead of guessing.
                throw new BridgeToolException(
                    GatewayErrorCodes.TargetNotReady,
                    "Could not read the file header of '" + Path.GetFileName(full) + "' (locked or unreadable), so it is unknown whether it is workshared. " +
                    "Not opening it in place; copy it again with sandbox_copy_model and retry.");
            }

            var options = new OpenOptions { Audit = audit };
            var detached = false;
            if (isWorkshared)
            {
                if (detach)
                {
                    options.DetachFromCentralOption = DetachFromCentralOption.DetachAndPreserveWorksets;
                    detached = true;
                }

                if (worksets != "last_viewed")
                {
                    options.SetOpenWorksetsConfiguration(new WorksetConfiguration(
                        worksets == "open_all" ? WorksetConfigurationOption.OpenAllWorksets : WorksetConfigurationOption.CloseAllWorksets));
                }
            }

            var modelPath = ModelPathUtils.ConvertUserVisiblePathToModelPath(full);
            var dialogSeq = ctx.Dialogs.LastSeq;
            var sw = Stopwatch.StartNew();
            Document? doc;
            if (activate)
            {
                doc = app.OpenAndActivateDocument(modelPath, options, false)?.Document;
            }
            else
            {
                doc = app.Application.OpenDocumentFile(modelPath, options);
            }

            sw.Stop();
            if (doc == null)
            {
                throw new BridgeToolException(GatewayErrorCodes.Conflict, "Revit did not open '" + full + "' (it returned no document). A dialog may have cancelled the open; check dialog_list_events.");
            }

            DocumentOrigins.Remember(doc, full);
            var result = new WireObject
            {
                { "opened", true },
                { "title", doc.Title },
                { "path", doc.PathName },
                { "is_active", ctx.IsActive(doc) },
                { "is_workshared", doc.IsWorkshared },
                { "detached_from_central", detached },
                { "saved_in_version", savedIn },
                { "upgraded", savedIn != null && savedIn != ctx.Runtime.RevitVersionNumber },
                { "audit", audit },
                { "elapsed_ms", sw.ElapsedMilliseconds },
                { "dialogs_during_open", ctx.Dialogs.After(dialogSeq, 20) },
            };
            if (string.IsNullOrEmpty(doc.PathName))
            {
                result["note"] = "Detached copy has no path until saved; refer to it by title or by the path it was opened from.";
            }

            return result;
        }

        public static object? Close(ToolContext ctx)
        {
            if (ctx.Args.Bool("save", false))
            {
                throw ToolArgs.Invalid("doc_close never saves: pass save=false (or omit it).");
            }

            var key = ctx.Args.Str("document") ?? ctx.Args.Str("path") ?? ctx.Args.Str("title");
            var doc = key == null
                ? ctx.ActiveDocument ?? throw new BridgeToolException(GatewayErrorCodes.NotFound, "No document is open.")
                : ctx.FindOpenDocument(key) ?? throw new BridgeToolException(GatewayErrorCodes.NotFound, "No open document matches '" + key + "'. Use doc_list_open.");
            if (doc.IsLinked)
            {
                throw ToolArgs.Invalid("'" + doc.Title + "' is a linked model; it closes with its host document.");
            }

            var openCount = CountOpen(ctx.App);
            WireObject? activated = null;
            if (ctx.IsActive(doc) && ctx.Args.Bool("activate_other", false))
            {
                var other = PickOtherToActivate(ctx, doc, ctx.Args.Str("activate_document"));
                if (other != null)
                {
                    // other is ALREADY open (picked from app.Documents just now, on Revit's API thread): this only switches the active
                    // document, it never opens a file.
                    ctx.App.OpenAndActivateDocument(other.PathName);
                    if (ctx.IsActive(other))
                    {
                        activated = new WireObject { { "title", other.Title }, { "path", other.PathName } };
                    }
                }
            }

            if (ctx.IsActive(doc))
            {
                throw new BridgeToolException(
                    GatewayErrorCodes.Conflict,
                    openCount <= 1
                        ? "'" + doc.Title + "' is the only open document and therefore the active one; Revit cannot close the active document through the API. " +
                          "Close Revit instead (revit_close), or open another document with doc_open(activate=true) first and then close this one."
                        : "'" + doc.Title + "' is the active document; Revit cannot close the active document through the API" +
                          (ctx.Args.Bool("activate_other", false)
                              ? " and no other open document could be activated (activate_other needs one saved to a local file). Close it from the Revit UI, or close Revit."
                              : ". Pass activate_other=true to switch to another open document first, or activate one yourself (doc_open with its path), then call doc_close again."),
                    new WireObject { { "title", doc.Title }, { "open_documents", openCount } });
            }

            var title = doc.Title;
            var path = doc.PathName;
            var modified = doc.IsModified;
            if (!doc.Close(false))
            {
                throw new BridgeToolException(GatewayErrorCodes.Conflict, "Revit refused to close '" + title + "'.");
            }

            return new WireObject
            {
                { "closed", true },
                { "title", title },
                { "path", path },
                { "discarded_changes", modified },
                { "saved", false },
                { "activated", activated },
            };
        }

        /// <summary>
        /// An open, non-linked document other than <paramref name="target"/> that can be made active by path: the one named by
        /// <paramref name="preferred"/> (title or path) when it qualifies, else the first that does. Only documents saved to a local
        /// file qualify (OpenAndActivateDocument takes a path; detached copies and cloud models have none).
        /// </summary>
        private static Document? PickOtherToActivate(ToolContext ctx, Document target, string? preferred)
        {
            bool Qualifies(Document? d) =>
                d != null && !d.IsLinked && !d.Equals(target) && !string.IsNullOrEmpty(d.PathName) && Path.IsPathRooted(d.PathName) && File.Exists(d.PathName);

            if (!string.IsNullOrWhiteSpace(preferred))
            {
                var named = ctx.FindOpenDocument(preferred!);
                if (Qualifies(named))
                {
                    return named;
                }
            }

            foreach (Document d in ctx.App.Application.Documents)
            {
                if (Qualifies(d))
                {
                    return d;
                }
            }

            return null;
        }

        private static int CountOpen(UIApplication app)
        {
            var n = 0;
            foreach (Document d in app.Application.Documents)
            {
                if (!d.IsLinked)
                {
                    n++;
                }
            }

            return n;
        }

        private static WireObject Units(Document doc)
        {
            var units = new WireObject();
            try
            {
                units["system"] = doc.DisplayUnitSystem.ToString();
                var u = doc.GetUnits();
                units["length"] = Unit(u, SpecTypeId.Length);
                units["area"] = Unit(u, SpecTypeId.Area);
                units["volume"] = Unit(u, SpecTypeId.Volume);
                units["angle"] = Unit(u, SpecTypeId.Angle);
            }
            catch (Exception)
            {
            }

            units["internal"] = "Raw 'value' doubles are in Revit internal units (feet, square feet, cubic feet, radians).";
            return units;
        }

        private static WireObject? Unit(Units units, ForgeTypeId spec)
        {
            try
            {
                var unit = units.GetFormatOptions(spec).GetUnitTypeId();
                return new WireObject { { "unit", LabelUtils.GetLabelForUnit(unit) }, { "type_id", unit.TypeId } };
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Full path for comparison, or null for cloud / non-filesystem paths (net48 GetFullPath throws on those).</summary>
        private static string? TryFullPath(string path)
        {
            try
            {
                return Path.GetFullPath(path);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
