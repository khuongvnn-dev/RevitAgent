namespace RevitAgent.Bridge.Tools
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.RegularExpressions;
    using Autodesk.Revit.DB;
    using Autodesk.Revit.UI;
    using Autodesk.Windows;
    using RevitAgent.Contracts.Bridge.Wire;
    using RevitAgent.Contracts.Protocol;
    using RibbonItem = Autodesk.Windows.RibbonItem;
    using RibbonPanel = Autodesk.Windows.RibbonPanel;

    /// <summary>ribbon_list_items, command_list_postable, command_post, view_activate, selection_get, selection_set.</summary>
    internal static class UiTools
    {
        private static readonly Regex Whitespace = new Regex(@"\s+", RegexOptions.Compiled);

        public static object? ListRibbon(ToolContext ctx)
        {
            var args = ctx.Args;
            var tabFilter = args.Str("tab");
            var panelFilter = args.Str("panel");
            var textFilter = args.Str("text_contains");
            var includeHidden = args.Bool("include_hidden", false);
            var postableOnly = args.Bool("postable_only", false);
            var limit = args.Limit();
            var offset = args.Offset();

            var ribbon = ComponentManager.Ribbon ?? throw new BridgeToolException(GatewayErrorCodes.TargetNotReady, "The Revit ribbon is not available yet (Revit still starting?).");
            var tabs = new List<object?>();
            var items = new List<object?>();
            var total = 0;
            foreach (var tab in ribbon.Tabs)
            {
                var tabTitle = Clean(tab.Title) ?? Clean(tab.Name) ?? tab.Id;
                var tabItems = 0;
                var tabMatches = tabFilter == null || Contains(tab.Id, tabFilter) || Contains(tabTitle, tabFilter);
                var walker = new List<(RibbonItem Item, string Panel, string? PanelId, string? Parent)>();
                foreach (var panel in tab.Panels)
                {
                    var source = panel.Source;
                    if (source == null || (!includeHidden && !panel.IsVisible))
                    {
                        continue;
                    }

                    var panelTitle = Clean(source.Title) ?? Clean(source.Name) ?? source.Id ?? string.Empty;
                    foreach (var item in source.Items)
                    {
                        Walk(item, panelTitle, source.Id, null, walker, 0);
                    }
                }

                foreach (var (item, panelTitle, panelId, parent) in walker)
                {
                    if (!includeHidden && !item.IsVisible)
                    {
                        continue;
                    }

                    tabItems++;
                    if (!tabMatches || (!includeHidden && !tab.IsVisible && tabFilter == null))
                    {
                        continue;
                    }

                    if (panelFilter != null && !Contains(panelTitle, panelFilter) && !Contains(panelId, panelFilter))
                    {
                        continue;
                    }

                    var text = Clean(item.Text) ?? Clean(item.Name);
                    var tooltip = TooltipText(item.ToolTip);
                    if (textFilter != null && !Contains(text, textFilter) && !Contains(item.Id, textFilter) && !Contains(tooltip, textFilter))
                    {
                        continue;
                    }

                    var commandId = string.IsNullOrEmpty(item.Id) ? null : item.Id;
                    bool? postable = null;
                    if (commandId != null && IsCommandKind(item))
                    {
                        try
                        {
                            postable = RevitCommandId.LookupCommandId(commandId) != null;
                        }
                        catch (Exception)
                        {
                            postable = false;
                        }
                    }

                    if (postableOnly && postable != true)
                    {
                        continue;
                    }

                    total++;
                    if (total <= offset || items.Count >= limit)
                    {
                        continue;
                    }

                    var o = new WireObject
                    {
                        { "tab", tabTitle },
                        { "panel", panelTitle },
                        { "id", item.Id },
                        { "text", text },
                        { "kind", item.GetType().Name },
                        { "enabled", item.IsEnabled },
                        { "visible", item.IsVisible },
                    };
                    if (tooltip != null)
                    {
                        o["tooltip"] = tooltip.Length > 300 ? tooltip.Substring(0, 300) + "..." : tooltip;
                    }

                    if (parent != null)
                    {
                        o["parent_id"] = parent;
                    }

                    if (postable.HasValue)
                    {
                        o["command_id"] = commandId;
                        o["postable"] = postable.Value;
                    }

                    items.Add(o);
                }

                tabs.Add(new WireObject
                {
                    { "id", tab.Id },
                    { "title", tabTitle },
                    { "visible", tab.IsVisible },
                    { "enabled", tab.IsEnabled },
                    { "contextual", tab.IsContextualTab },
                    { "panel_count", tab.Panels.Count },
                    { "item_count", tabItems },
                });
            }

            return new WireObject
            {
                { "tabs", tabs },
                { "total", total },
                { "offset", offset },
                { "returned", items.Count },
                { "truncated", offset + items.Count < total },
                { "items", items },
                { "hint", "Pass an item's command_id to command_post to run it (when postable is true)." },
            };
        }

        public static object? ListPostable(ToolArgs args)
        {
            var nameContains = args.Str("name_contains");
            var limit = args.Limit();
            var names = Enum.GetNames(typeof(PostableCommand))
                .Where(n => nameContains == null || n.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return new WireObject
            {
                { "total", names.Count },
                { "returned", Math.Min(limit, names.Count) },
                { "commands", names.Take(limit).Cast<object?>().ToList() },
            };
        }

        public static object? PostCommand(ToolContext ctx)
        {
            var command = ctx.Args.ReqStr("command").Trim();
            if (command.Length == 0)
            {
                throw ToolArgs.Invalid("'command' must not be empty.");
            }

            RefuseBlocked(RevitAgent.Contracts.Bridge.CommandSafety.BlockReason(command));

            // An exact name only: Enum.TryParse would read "Wall,Save" as Wall|Save and "+12" as the value 12.
            RevitCommandId? id;
            string resolvedAs;
            var postableName = Enum.GetNames(typeof(PostableCommand)).FirstOrDefault(n => string.Equals(n, command, StringComparison.OrdinalIgnoreCase));
            if (postableName != null)
            {
                var postable = (PostableCommand)Enum.Parse(typeof(PostableCommand), postableName);
                RefuseBlocked(RevitAgent.Contracts.Bridge.CommandSafety.BlockReason(postable.ToString()));
                id = RevitCommandId.LookupPostableCommandId(postable);
                resolvedAs = "postable_command";
            }
            else
            {
                id = RevitCommandId.LookupCommandId(command);
                resolvedAs = "command_id";
            }

            if (id == null)
            {
                throw new BridgeToolException(
                    GatewayErrorCodes.NotFound,
                    "Unknown command '" + command + "'. Pass a PostableCommand name (command_list_postable) or a ribbon item's command_id (ribbon_list_items), e.g. ID_OBJECTS_WALL or CustomCtrl_%CustomCtrl_%Tab%Panel%Button.");
            }

            // What Revit resolved (e.g. ID_FILE_SAVE) is checked too, whatever the agent's text looked like.
            RefuseBlocked(RevitAgent.Contracts.Bridge.CommandSafety.ResolvedIdBlockReason(id.Name));

            if (!ctx.App.CanPostCommand(id))
            {
                throw new BridgeToolException(
                    GatewayErrorCodes.Conflict,
                    "Revit cannot post '" + command + "' right now (no document open, command not available in this context, or another posted command is still pending). Observe the UI and retry.");
            }

            try
            {
                ctx.App.PostCommand(id);
            }
            catch (Autodesk.Revit.Exceptions.InvalidOperationException ex)
            {
                throw new BridgeToolException(GatewayErrorCodes.Busy, "Revit refused to post '" + command + "': " + ex.Message);
            }

            return new WireObject
            {
                { "posted", true },
                { "command_id", id.Name },
                { "numeric_id", id.Id },
                { "resolved_as", resolvedAs },
                { "note", "The command starts after this call returns, when Revit is idle. Observe the UI next (ui_snapshot / revit_list_dialogs / dialog_list_events) to see what it opened." },
            };
        }

        public static object? ActivateView(ToolContext ctx)
        {
            var uidoc = ctx.App.ActiveUIDocument ?? throw new BridgeToolException(GatewayErrorCodes.TargetNotReady, "No document is open; open one with doc_open first.");
            var doc = uidoc.Document;
            var key = ctx.Args.Str("document");
            if (key != null)
            {
                var target = ctx.FindOpenDocument(key);
                if (target == null || !target.Equals(doc))
                {
                    throw new BridgeToolException(GatewayErrorCodes.Conflict, "view_activate works on the active document only ('" + doc.Title + "'). Activate the other document with doc_open(path, activate=true) first.");
                }
            }

            View view;
            var id = ctx.Args.Long("view_id");
            if (id.HasValue)
            {
                view = doc.GetElement(Ids.Make(id.Value)) as View
                    ?? throw new BridgeToolException(GatewayErrorCodes.NotFound, "Element " + id.Value + " is not a view in '" + doc.Title + "'. Find views with model_query_elements(class: \"View3D\" or \"ViewPlan\" or \"ViewSection\", fields: [\"Name\"]).");
            }
            else
            {
                var name = ctx.Args.Str("name") ?? throw ToolArgs.Invalid("Pass 'view_id' or 'name'.");
                var kind = ctx.Args.Str("type");
                var matches = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                    .Where(v => !v.IsTemplate && string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase))
                    .Where(v => kind == null || string.Equals(ModelTools.KindOf(v.ViewType), kind, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (matches.Count == 0)
                {
                    throw new BridgeToolException(GatewayErrorCodes.NotFound, "No view named '" + name + "'. Find views with model_query_elements(class: \"View3D\" or \"ViewPlan\" or \"ViewSection\", fields: [\"Name\"]).");
                }

                if (matches.Count > 1)
                {
                    throw new BridgeToolException(GatewayErrorCodes.Conflict, matches.Count + " views are named '" + name + "'; pass view_id or type.",
                        new WireObject { { "views", matches.Select(v => (object?)Describe.View(v)).ToList() } });
                }

                view = matches[0];
            }

            if (view.IsTemplate)
            {
                throw ToolArgs.Invalid("'" + view.Name + "' is a view template and cannot be opened.");
            }

            var previous = uidoc.ActiveView;
            uidoc.ActiveView = view;
            return new WireObject
            {
                { "activated", true },
                { "view", Describe.View(view) },
                { "previous", previous == null ? null : Describe.View(previous) },
            };
        }

        public static object? GetSelection(ToolContext ctx)
        {
            var uidoc = ctx.App.ActiveUIDocument ?? throw new BridgeToolException(GatewayErrorCodes.TargetNotReady, "No document is open.");
            var limit = ctx.Args.Limit();
            var ids = uidoc.Selection.GetElementIds();
            var items = new List<object?>();
            foreach (var id in ids)
            {
                if (items.Count >= limit)
                {
                    break;
                }

                var e = uidoc.Document.GetElement(id);
                items.Add(e == null ? new WireObject { { "id", Ids.Of(id) } } : Describe.Brief(e));
            }

            return new WireObject { { "document", uidoc.Document.Title }, { "total", ids.Count }, { "returned", items.Count }, { "elements", items } };
        }

        public static object? SetSelection(ToolContext ctx)
        {
            var uidoc = ctx.App.ActiveUIDocument ?? throw new BridgeToolException(GatewayErrorCodes.TargetNotReady, "No document is open.");
            if (!ctx.Args.Has("ids"))
            {
                throw ToolArgs.Invalid("Pass 'ids' (array of element ids; [] clears the selection).");
            }

            var doc = uidoc.Document;
            var wanted = ctx.Args.IdList("ids");
            var valid = new List<ElementId>();
            var missing = new List<object?>();
            foreach (var raw in wanted.Distinct())
            {
                var id = Ids.Make(raw);
                if (doc.GetElement(id) == null)
                {
                    missing.Add(raw);
                }
                else
                {
                    valid.Add(id);
                }
            }

            if (missing.Count > 0)
            {
                throw new BridgeToolException(GatewayErrorCodes.NotFound, missing.Count + " id(s) do not exist in '" + doc.Title + "'; selection unchanged.", new WireObject { { "missing", missing } });
            }

            uidoc.Selection.SetElementIds(valid);
            return new WireObject { { "selected", valid.Count }, { "document", doc.Title } };
        }

        private static void Walk(RibbonItem item, string panel, string? panelId, string? parent, List<(RibbonItem, string, string?, string?)> output, int depth)
        {
            if (item == null || depth > 6 || item is RibbonSeparator || item is RibbonPanelBreak || item is RibbonRowBreak)
            {
                return;
            }

            if (item is RibbonRowPanel row)
            {
                foreach (var sub in row.Items)
                {
                    Walk(sub, panel, panelId, parent, output, depth + 1);
                }

                return;
            }

            output.Add((item, panel, panelId, parent));
            if (item is RibbonListButton list)
            {
                foreach (var sub in list.Items)
                {
                    Walk(sub, panel, panelId, item.Id, output, depth + 1);
                }
            }
        }

        private static bool IsCommandKind(RibbonItem item) =>
            !(item is RibbonLabel) && !(item is RibbonCombo) && !(item is RibbonTextBox);

        private static void RefuseBlocked(string? reason)
        {
            if (reason != null)
            {
                throw new BridgeToolException(GatewayErrorCodes.PermissionDenied, reason);
            }
        }

        private static bool Contains(string? haystack, string needle) =>
            haystack != null && haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

        private static string? Clean(string? s)
        {
            if (string.IsNullOrWhiteSpace(s))
            {
                return null;
            }

            return Whitespace.Replace(s, " ").Trim();
        }

        private static string? TooltipText(object? tooltip)
        {
            switch (tooltip)
            {
                case null:
                    return null;
                case string s:
                    return Clean(s);
                case RibbonToolTip rt:
                    var title = Clean(rt.Title);
                    var content = Clean(rt.Content as string);
                    return title == null ? content : content == null ? title : title + ": " + content;
                default:
                    return Clean(tooltip.ToString());
            }
        }
    }
}
