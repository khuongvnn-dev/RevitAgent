namespace RevitAgent.Bridge.Tools
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using RevitAgent.Bridge.Runtime;
    using RevitAgent.Contracts.Bridge;
    using RevitAgent.Contracts.Bridge.Wire;
    using RevitAgent.Contracts.Protocol;

    /// <summary>
    /// Name → handler table of the Bridge. Immediate tools run on the pipe thread (no Revit API);
    /// Revit tools run on the UI thread through <see cref="RevitExecutor"/>.
    /// </summary>
    internal sealed class BridgeToolRegistry
    {
        private readonly Dictionary<string, Func<ToolContext, object?>> _revit = new Dictionary<string, Func<ToolContext, object?>>(StringComparer.Ordinal);
        private readonly Dictionary<string, Func<ToolArgs, BridgeServerCore, object?>> _immediate = new Dictionary<string, Func<ToolArgs, BridgeServerCore, object?>>(StringComparer.Ordinal);

        public BridgeToolRegistry(BridgeRuntimeInfo runtime, DialogEventRecorder dialogs)
        {
            Runtime = runtime;
            Dialogs = dialogs;

            _immediate[BridgeToolNames.Ping] = Ping;
            _immediate[BridgeToolNames.DialogListEvents] = (args, _) => ListDialogEvents(args);
            _immediate[BridgeToolNames.CommandListPostable] = (args, _) => UiTools.ListPostable(args);

            _revit[BridgeToolNames.DocInfo] = DocumentTools.Info;
            _revit[BridgeToolNames.DocListOpen] = DocumentTools.ListOpen;
            _revit[BridgeToolNames.DocOpen] = DocumentTools.Open;
            _revit[BridgeToolNames.DocClose] = DocumentTools.Close;
            _revit[BridgeToolNames.ModelListCategories] = ModelTools.ListCategories;
            _revit[BridgeToolNames.ModelQueryElements] = ModelTools.QueryElements;
            _revit[BridgeToolNames.ModelGetElement] = ModelTools.GetElement;
            _revit[BridgeToolNames.ModelListViews] = ModelTools.ListViews;
            _revit[BridgeToolNames.ModelGetWarnings] = ModelTools.GetWarnings;
            _revit[BridgeToolNames.ScheduleListSchedulableCategories] = ScheduleTools.ListSchedulableCategories;
            _revit[BridgeToolNames.ScheduleRead] = ScheduleTools.Read;
            _revit[BridgeToolNames.ScheduleCreate] = ScheduleTools.Create;
            _revit[BridgeToolNames.RibbonListItems] = UiTools.ListRibbon;
            _revit[BridgeToolNames.CommandPost] = UiTools.PostCommand;
            _revit[BridgeToolNames.ViewActivate] = UiTools.ActivateView;
            _revit[BridgeToolNames.SelectionGet] = UiTools.GetSelection;
            _revit[BridgeToolNames.SelectionSet] = UiTools.SetSelection;

            Names = _immediate.Keys.Concat(_revit.Keys).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        }

        public BridgeRuntimeInfo Runtime { get; }

        public DialogEventRecorder Dialogs { get; }

        public IReadOnlyCollection<string> Names { get; }

        public BridgeToolKind GetKind(string tool) =>
            _immediate.ContainsKey(tool) ? BridgeToolKind.Immediate : _revit.ContainsKey(tool) ? BridgeToolKind.Revit : BridgeToolKind.Unknown;

        public object? RunImmediate(string tool, ToolArgs args, BridgeServerCore server) =>
            _immediate.TryGetValue(tool, out var handler)
                ? handler(args, server)
                : throw new BridgeToolException(GatewayErrorCodes.NotFound, "Unknown Bridge tool '" + tool + "'.");

        public Func<ToolContext, object?> GetRevitHandler(string tool) =>
            _revit.TryGetValue(tool, out var handler)
                ? handler
                : throw new BridgeToolException(GatewayErrorCodes.NotFound, "Unknown Bridge tool '" + tool + "'.");

        private object? Ping(ToolArgs args, BridgeServerCore server)
        {
            var busyTool = server.BusyTool;
            var last = Dialogs.After(Dialogs.LastSeq - 1, 1);
            return new WireObject
            {
                { "protocol", BridgeWire.ProtocolVersion },
                { "bridge_version", Runtime.BridgeVersion },
                { "pid", Runtime.Pid },
                { "process_start", Runtime.ProcessStart?.ToString("o") },
                { "revit", Runtime.RevitInfo() },
                { "framework", Runtime.Framework },
                { "assembly", Runtime.AssemblyPath },
                { "pipe", server.PipeName },
                { "started_at", Runtime.StartedUtc.ToLocalTime().ToString("o") },
                { "uptime_sec", Math.Round((DateTime.UtcNow - Runtime.StartedUtc).TotalSeconds, 1) },
                { "busy", busyTool != null },
                { "busy_tool", busyTool },
                { "busy_sec", busyTool != null ? Math.Round(server.BusySeconds, 1) : 0.0 },
                { "requests", server.RequestCount },
                { "dialog_events", Dialogs.TotalCount },
                { "last_dialog", last.Count > 0 ? last[0] : null },
                { "log_file", Runtime.LogFile },
                { "tools", Names.Where(n => n != BridgeToolNames.Ping).Cast<object?>().ToList() },
            };
        }

        private object? ListDialogEvents(ToolArgs args)
        {
            var afterSeq = args.Long("after_seq") ?? 0;
            var limit = args.Limit("limit", 50, DialogEventRecorder.Capacity);
            var events = Dialogs.After(afterSeq, limit);
            return new WireObject
            {
                { "last_seq", Dialogs.LastSeq },
                { "returned", events.Count },
                { "events", events },
                { "note", "Recorded from Revit's DialogBoxShowing event (record only; the Bridge never answers dialogs). Pass after_seq=last_seq to poll for new ones." },
            };
        }
    }
}
