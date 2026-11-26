namespace RevitAgent.Host.Services
{
    using System;
    using System.Collections.ObjectModel;
    using System.Linq;
    using System.Text.Json;
    using System.Windows;
    using System.Windows.Media;
    using System.Windows.Threading;
    using RevitAgent.Host.Models;

    public sealed class AuditLogService : IAuditLogService
    {
        private const int MaxLogCapacity = 500;
        private readonly object _lock = new();

        public ObservableCollection<LogEventItem> EventLogs { get; } = new();

        public event Action<LogEventItem>? LogAdded;

        public void AddLog(string category, string action, string details)
        {
            var item = new LogEventItem
            {
                Timestamp = DateTime.Now.ToString("HH:mm:ss.fff"),
                Category = category,
                Action = action,
                Details = details,
                BadgeColor = GetBadgeColor(category)
            };

            void DispatchAdd()
            {
                lock (_lock)
                {
                    EventLogs.Insert(0, item);
                    if (EventLogs.Count > MaxLogCapacity)
                    {
                        EventLogs.RemoveAt(EventLogs.Count - 1);
                    }
                }
                LogAdded?.Invoke(item);
            }

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                // Never block a background thread on the UI thread (it deadlocked shutdown, 2026-11-20).
                if (!dispatcher.HasShutdownStarted)
                {
                    dispatcher.BeginInvoke(DispatchAdd);
                }
            }
            else
            {
                DispatchAdd();
            }
        }

        public void ClearLogs()
        {
            void DispatchClear()
            {
                lock (_lock)
                {
                    EventLogs.Clear();
                }
            }

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                // Never block a background thread on the UI thread (it deadlocked shutdown, 2026-11-20).
                if (!dispatcher.HasShutdownStarted)
                {
                    dispatcher.BeginInvoke(DispatchClear);
                }
            }
            else
            {
                DispatchClear();
            }
        }

        public string ExportLogsAsJson()
        {
            lock (_lock)
            {
                var exportData = EventLogs.Select(l => new
                {
                    l.Timestamp,
                    l.Category,
                    l.Action,
                    l.Details
                });

                return JsonSerializer.Serialize(exportData, new JsonSerializerOptions { WriteIndented = true });
            }
        }

        private static readonly Brush FastPathBrush = Freeze(new SolidColorBrush(Color.FromRgb(59, 130, 246)));
        private static readonly Brush AutomationBrush = Freeze(new SolidColorBrush(Color.FromRgb(245, 158, 11)));
        private static readonly Brush ErrorBrush = Freeze(new SolidColorBrush(Color.FromRgb(239, 68, 68)));
        private static readonly Brush WarnBrush = Freeze(new SolidColorBrush(Color.FromRgb(217, 119, 6)));
        private static readonly Brush IpcBrush = Freeze(new SolidColorBrush(Color.FromRgb(16, 185, 129)));
        private static readonly Brush SandboxBrush = Freeze(new SolidColorBrush(Color.FromRgb(168, 85, 247)));
        private static readonly Brush ConfigBrush = Freeze(new SolidColorBrush(Color.FromRgb(99, 102, 241)));
        private static readonly Brush PreflightBrush = Freeze(new SolidColorBrush(Color.FromRgb(14, 165, 233)));
        private static readonly Brush DefaultBrush = Freeze(new SolidColorBrush(Color.FromRgb(71, 85, 105)));

        private static Brush Freeze(Freezable freezable)
        {
            freezable.Freeze();
            return (Brush)freezable;
        }

        public static Brush GetBadgeColor(string category)
        {
            return category switch
            {
                "FASTPATH" => FastPathBrush,
                "AUTOMATION" => AutomationBrush,
                "ERROR" => ErrorBrush,
                "WARN" => WarnBrush,
                "IPC" or "IPC_REQ" or "IPC_RESP" or "MCP" or "BRIDGE" or "TOOL" => IpcBrush,
                "SANDBOX" => SandboxBrush,
                "CONFIG" => ConfigBrush,
                "PREFLIGHT" => PreflightBrush,
                _ => DefaultBrush
            };
        }
    }
}
