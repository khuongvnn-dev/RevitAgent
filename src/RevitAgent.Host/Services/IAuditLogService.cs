namespace RevitAgent.Host.Services
{
    using System;
    using System.Collections.ObjectModel;
    using RevitAgent.Host.Models;

    public interface IAuditLogService
    {
        ObservableCollection<LogEventItem> EventLogs { get; }
        event Action<LogEventItem>? LogAdded;
        void AddLog(string category, string action, string details);
        void ClearLogs();
        string ExportLogsAsJson();
    }
}
