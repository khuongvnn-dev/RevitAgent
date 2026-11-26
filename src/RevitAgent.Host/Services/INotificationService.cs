namespace RevitAgent.Host.Services
{
    using System;
    using System.Collections.ObjectModel;
    using System.Windows.Input;
    using RevitAgent.Host.Models;

    /// <summary>
    /// Thread-safe service for presenting in-app floating toasts and tracking persistent notification history.
    /// </summary>
    public interface INotificationService
    {
        /// <summary>
        /// Currently active toasts visible on screen (floating stack).
        /// </summary>
        ObservableCollection<NotificationItem> ActiveToasts { get; }

        /// <summary>
        /// All historical notifications recorded during this session.
        /// </summary>
        ObservableCollection<NotificationItem> History { get; }

        /// <summary>
        /// Number of unread notifications in history.
        /// </summary>
        int UnreadCount { get; }

        /// <summary>
        /// Default duration in seconds before a toast auto-dismisses.
        /// </summary>
        int DefaultDurationSeconds { get; set; }

        /// <summary>
        /// Event fired when a new notification is added.
        /// </summary>
        event Action<NotificationItem>? NotificationAdded;

        /// <summary>
        /// Event fired when a toast is dismissed.
        /// </summary>
        event Action<NotificationItem>? ToastDismissed;

        /// <summary>
        /// Displays a generic notification.
        /// </summary>
        NotificationItem Show(
            NotificationType type,
            string title,
            string message,
            int? durationSeconds = null,
            ICommand? action = null,
            string? actionLabel = null);

        /// <summary>
        /// Displays a Success toast notification.
        /// </summary>
        NotificationItem ShowSuccess(
            string title,
            string message,
            int? durationSeconds = null,
            ICommand? action = null,
            string? actionLabel = null);

        /// <summary>
        /// Displays an Info toast notification.
        /// </summary>
        NotificationItem ShowInfo(
            string title,
            string message,
            int? durationSeconds = null,
            ICommand? action = null,
            string? actionLabel = null);

        /// <summary>
        /// Displays a Warning toast notification.
        /// </summary>
        NotificationItem ShowWarning(
            string title,
            string message,
            int? durationSeconds = null,
            ICommand? action = null,
            string? actionLabel = null);

        /// <summary>
        /// Displays an Error toast notification.
        /// </summary>
        NotificationItem ShowError(
            string title,
            string message,
            int? durationSeconds = null,
            ICommand? action = null,
            string? actionLabel = null);

        /// <summary>
        /// Dismisses an active floating toast by ID.
        /// </summary>
        void DismissToast(Guid id);

        /// <summary>
        /// Dismisses an active floating toast.
        /// </summary>
        void DismissToast(NotificationItem item);

        /// <summary>
        /// Marks all history notifications as read.
        /// </summary>
        void MarkAllAsRead();

        /// <summary>
        /// Clears all historical notifications and active toasts.
        /// </summary>
        void ClearHistory();
    }
}
