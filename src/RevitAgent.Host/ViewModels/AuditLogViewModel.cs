namespace RevitAgent.Host.ViewModels
{
    using System;
    using System.Collections.ObjectModel;
    using System.ComponentModel;
    using System.Linq;
    using System.Windows.Data;
    using CommunityToolkit.Mvvm.ComponentModel;
    using CommunityToolkit.Mvvm.Input;
    using RevitAgent.Host.Models;
    using RevitAgent.Host.Services;

    public partial class AuditLogViewModel : ObservableObject
    {
        private readonly IAuditLogService _auditLogService;
        private readonly ILocalizationService _localizationService;
        private readonly ICollectionView _logsView;

        [ObservableProperty]
        private string _searchText = string.Empty;

        [ObservableProperty]
        private string _currentLogLevelFilter = "ALL";

        [ObservableProperty]
        private string _logCountDisplay = "0 events";

        public ObservableCollection<LogEventItem> EventLogs => _auditLogService.EventLogs;
        public ICollectionView LogsView => _logsView;

        public AuditLogViewModel(
            IAuditLogService auditLogService,
            ILocalizationService? localizationService = null)
        {
            _auditLogService = auditLogService ?? throw new ArgumentNullException(nameof(auditLogService));
            _localizationService = localizationService ?? new LocalizationService();

            _logsView = CollectionViewSource.GetDefaultView(_auditLogService.EventLogs);
            _logsView.Filter = FilterLogItem;

            _auditLogService.LogAdded += OnLogAdded;
            _localizationService.LanguageChanged += OnLanguageChanged;

            UpdateLogCountDisplay();
        }

        private void OnLanguageChanged(string lang)
        {
            UpdateLogCountDisplay();
        }

        private void OnLogAdded(LogEventItem item)
        {
            UpdateLogCountDisplay();
        }

        partial void OnSearchTextChanged(string value)
        {
            _logsView.Refresh();
            UpdateLogCountDisplay();
        }

        partial void OnCurrentLogLevelFilterChanged(string value)
        {
            _logsView.Refresh();
            UpdateLogCountDisplay();
        }

        [RelayCommand]
        public void FilterLogLevel(string level)
        {
            if (string.IsNullOrWhiteSpace(level)) level = "ALL";
            CurrentLogLevelFilter = level.ToUpperInvariant();
        }

        [RelayCommand]
        public void ClearLogs()
        {
            _auditLogService.ClearLogs();
            UpdateLogCountDisplay();
        }

        [RelayCommand]
        public void ExportLogs()
        {
            try
            {
                string json = _auditLogService.ExportLogsAsJson();
                var saveDlg = new Microsoft.Win32.SaveFileDialog
                {
                    Title = _localizationService.GetString("Str_DlgExportLogsTitle"),
                    Filter = "JSON Files (*.json)|*.json|All Files (*.*)|*.*",
                    FileName = $"audit_log_{DateTime.Now:yyyyMMdd_HHmmss}.json"
                };

                if (saveDlg.ShowDialog() == true)
                {
                    System.IO.File.WriteAllText(saveDlg.FileName, json);
                    _auditLogService.AddLog("SYSTEM", "Export Logs", $"Exported {EventLogs.Count} events to: {saveDlg.FileName}");
                }
            }
            catch (Exception ex)
            {
                _auditLogService.AddLog("ERROR", "Export Failed", ex.Message);
            }
        }

        private bool FilterLogItem(object obj)
        {
            if (obj is not LogEventItem item) return false;

            bool levelMatch = CurrentLogLevelFilter switch
            {
                "INFO" => item.Category is "INFO" or "SYSTEM" or "STATUS" or "FASTPATH" or "AUTOMATION" or "SANDBOX" or "PREFLIGHT" or "CONFIG",
                "WARN" => item.Category is "WARN" or "ALERT",
                "ERROR" => item.Category is "ERROR" or "FATAL",
                "IPC" => item.Category is "IPC" or "IPC_REQ" or "IPC_RESP" or "FASTPATH",
                _ => true
            };

            if (!levelMatch) return false;

            if (string.IsNullOrWhiteSpace(SearchText)) return true;

            string search = SearchText.Trim();
            return (item.Action?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                   (item.Details?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                   (item.Category?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false);
        }

        public void UpdateLogCountDisplay()
        {
            int total = EventLogs.Count;
            int visible = _logsView?.Cast<object>().Count() ?? 0;
            string fmt = _localizationService.GetString("Str_EventsCount");
            LogCountDisplay = (visible == total) 
                ? string.Format(fmt, total) 
                : $"{visible} / {string.Format(fmt, total)}";
        }
    }
}
