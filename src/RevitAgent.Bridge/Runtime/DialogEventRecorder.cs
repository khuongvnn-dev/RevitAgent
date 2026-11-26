namespace RevitAgent.Bridge.Runtime
{
    using System;
    using System.Collections.Generic;
    using Autodesk.Revit.UI.Events;
    using RevitAgent.Contracts.Bridge.Wire;

    /// <summary>
    /// Records Revit <c>DialogBoxShowing</c> events into a ring buffer so an agent can see which dialogs
    /// Revit raised (and during which Bridge call). It ONLY records: it never calls OverrideResult, so
    /// every dialog behaves exactly as it would for the user (the v1 global dialog suppressor is gone).
    /// Note: the Revit API does not expose a dialog's buttons; use revit_list_dialogs / ui_snapshot for those.
    /// </summary>
    internal sealed class DialogEventRecorder
    {
        public const int Capacity = 200;

        private readonly object _gate = new object();
        private readonly Queue<WireObject> _events = new Queue<WireObject>();
        private long _seq;

        /// <summary>Returns the Bridge tool currently running (if any) for correlation.</summary>
        public Func<string?>? CurrentTool { get; set; }

        public long LastSeq
        {
            get
            {
                lock (_gate)
                {
                    return _seq;
                }
            }
        }

        public long TotalCount => LastSeq;

        /// <summary>Revit event handler (UI thread). Never throws, never overrides the dialog.</summary>
        public void OnDialogBoxShowing(object? sender, DialogBoxShowingEventArgs e)
        {
            try
            {
                string kind;
                string? message = null;
                object? dialogType = null;
                if (e is TaskDialogShowingEventArgs td)
                {
                    kind = "task_dialog";
                    message = td.Message;
                }
                else if (e is MessageBoxShowingEventArgs mb)
                {
                    kind = "message_box";
                    message = mb.Message;
                    dialogType = mb.DialogType;
                }
                else
                {
                    kind = "dialog";
                }

                string? during = null;
                try
                {
                    during = CurrentTool?.Invoke();
                }
                catch (Exception)
                {
                }

                Record(kind, e.DialogId, message, dialogType, during);
            }
            catch (Exception)
            {
                // Never throw into Revit.
            }
        }

        public void Record(string kind, string? dialogId, string? message, object? dialogType, string? duringTool)
        {
            lock (_gate)
            {
                _seq++;
                var record = new WireObject
                {
                    { "seq", _seq },
                    { "time", DateTimeOffset.Now.ToString("o") },
                    { "kind", kind },
                    { "dialog_id", dialogId },
                    { "message", Clip(message, 2000) },
                };
                if (dialogType != null)
                {
                    record["message_box_type"] = dialogType;
                }

                if (duringTool != null)
                {
                    record["during_tool"] = duringTool;
                }

                _events.Enqueue(record);
                while (_events.Count > Capacity)
                {
                    _events.Dequeue();
                }
            }
        }

        /// <summary>Events with seq &gt; <paramref name="afterSeq"/>, oldest first, at most <paramref name="limit"/> (the newest ones).</summary>
        public List<WireObject> After(long afterSeq, int limit)
        {
            lock (_gate)
            {
                var all = new List<WireObject>();
                foreach (var e in _events)
                {
                    if ((long)e["seq"]! > afterSeq)
                    {
                        all.Add(e);
                    }
                }

                return all.Count <= limit ? all : all.GetRange(all.Count - limit, limit);
            }
        }

        private static string? Clip(string? s, int max) => s == null || s.Length <= max ? s : s.Substring(0, max) + "...";
    }
}
