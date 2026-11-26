namespace RevitAgent.Host.ViewModels
{
    using System;
    using System.Collections.ObjectModel;
    using System.ComponentModel;
    using System.Windows.Data;
    using CommunityToolkit.Mvvm.ComponentModel;
    using CommunityToolkit.Mvvm.Input;
    using RevitAgent.Host.Models;
    using RevitAgent.Host.Services;

    public partial class NotificationsViewModel : ObservableObject
    {
        private readonly INotificationService _notificationService;
        private readonly ILocalizationService _localizationService;
        private readonly ICollectionView _notificationsView;

        [ObservableProperty]
        private string _currentLevelFilter = "ALL";

        [ObservableProperty]
        private int _unreadCount;

        [ObservableProperty]
        private string _notificationCountDisplay = "0 notifications";

        [ObservableProperty]
        private bool _hasNotifications;

        public ObservableCollection<NotificationItem> History => _notificationService.History;
        public ObservableCollection<NotificationItem> ActiveToasts => _notificationService.ActiveToasts;
        public ICollectionView NotificationsView => _notificationsView;

        public NotificationsViewModel(
            INotificationService notificationService,
            ILocalizationService? localizationService = null)
        {
            _notificationService = notificationService ?? throw new ArgumentNullException(nameof(notificationService));
            _localizationService = localizationService ?? new LocalizationService();

            _notificationsView = CollectionViewSource.GetDefaultView(_notificationService.History);
            _notificationsView.Filter = FilterNotificationItem;

            _notificationService.NotificationAdded += OnNotificationChanged;
            _notificationService.ToastDismissed += OnNotificationChanged;
            _localizationService.LanguageChanged += OnLanguageChanged;

            UpdateCounts();
        }

        private void OnLanguageChanged(string lang)
        {
            UpdateCounts();
        }

        private void OnNotificationChanged(NotificationItem item)
        {
            UpdateCounts();
        }

        private bool FilterNotificationItem(object obj)
        {
            if (obj is not NotificationItem item) return false;

            if (string.Equals(CurrentLevelFilter, "ALL", StringComparison.OrdinalIgnoreCase))
                return true;

            return CurrentLevelFilter.ToUpperInvariant() switch
            {
                "SUCCESS" => item.Type == NotificationType.Success,
                "INFO" => item.Type == NotificationType.Info,
                "WARN" or "WARNING" => item.Type == NotificationType.Warning,
                "ERROR" => item.Type == NotificationType.Error,
                _ => true
            };
        }

        partial void OnCurrentLevelFilterChanged(string value)
        {
            _notificationsView.Refresh();
            UpdateCounts();
        }

        public void UpdateCounts()
        {
            UnreadCount = _notificationService.UnreadCount;
            HasNotifications = _notificationService.History.Count > 0;

            int total = _notificationService.History.Count;
            string template = _localizationService.GetString("Str_NotificationCountFmt");
            if (string.IsNullOrWhiteSpace(template) || template.StartsWith("Str_"))
            {
                template = "{0} notifications";
            }
            NotificationCountDisplay = string.Format(template, total);
        }

        [RelayCommand]
        public void FilterLevel(string level)
        {
            if (string.IsNullOrWhiteSpace(level)) level = "ALL";
            CurrentLevelFilter = level.ToUpperInvariant();
        }

        [RelayCommand]
        public void ClearAll()
        {
            _notificationService.ClearHistory();
            _notificationsView.Refresh();
            UpdateCounts();
        }

        [RelayCommand]
        public void MarkAllAsRead()
        {
            _notificationService.MarkAllAsRead();
            UpdateCounts();
        }

        [RelayCommand]
        public void DismissToast(NotificationItem? item)
        {
            if (item != null)
            {
                _notificationService.DismissToast(item);
                item.IsRead = true;
                UpdateCounts();
            }
        }
    }
}
