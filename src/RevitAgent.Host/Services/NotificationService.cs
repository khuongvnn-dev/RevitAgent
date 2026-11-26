namespace RevitAgent.Host.Services
{
    using System;
    using System.Collections.ObjectModel;
    using System.Linq;
    using System.Threading.Tasks;
    using System.Windows;
    using System.Windows.Input;
    using System.Windows.Threading;
    using RevitAgent.Host.Models;

    /// <summary>
    /// Thread-safe implementation of <see cref="INotificationService"/>.
    /// Handles floating toast stacking, automatic dismissal with cancellation, and persistent history.
    /// </summary>
    public sealed class NotificationService : INotificationService, IDisposable
    {
        private const int MaxActiveToasts = 4;
        private const int MaxHistoryItems = 500;

        private readonly object _syncLock = new();
        private readonly ObservableCollection<NotificationItem> _activeToasts = new();
        private readonly ObservableCollection<NotificationItem> _history = new();
        private int _defaultDurationSeconds = 3;
        private bool _disposed;

        public ObservableCollection<NotificationItem> ActiveToasts => _activeToasts;
        public ObservableCollection<NotificationItem> History => _history;

        public int UnreadCount
        {
            get
            {
                lock (_syncLock)
                {
                    return _history.Count(n => !n.IsRead);
                }
            }
        }

        public int DefaultDurationSeconds
        {
            get => _defaultDurationSeconds;
            set => _defaultDurationSeconds = value <= 0 ? 3 : value;
        }

        public event Action<NotificationItem>? NotificationAdded;
        public event Action<NotificationItem>? ToastDismissed;

        public NotificationService(int defaultDurationSeconds = 3)
        {
            _defaultDurationSeconds = defaultDurationSeconds <= 0 ? 3 : defaultDurationSeconds;
            try
            {
                System.Windows.Data.BindingOperations.EnableCollectionSynchronization(_activeToasts, _syncLock);
                System.Windows.Data.BindingOperations.EnableCollectionSynchronization(_history, _syncLock);
            }
            catch
            {
                // Headless test runner fallback
            }
        }

        public NotificationItem ShowSuccess(
            string title,
            string message,
            int? durationSeconds = null,
            ICommand? action = null,
            string? actionLabel = null)
        {
            return Show(NotificationType.Success, title, message, durationSeconds, action, actionLabel);
        }

        public NotificationItem ShowInfo(
            string title,
            string message,
            int? durationSeconds = null,
            ICommand? action = null,
            string? actionLabel = null)
        {
            return Show(NotificationType.Info, title, message, durationSeconds, action, actionLabel);
        }

        public NotificationItem ShowWarning(
            string title,
            string message,
            int? durationSeconds = null,
            ICommand? action = null,
            string? actionLabel = null)
        {
            // Warnings get slightly longer visibility if not explicitly specified
            int dur = durationSeconds ?? Math.Max(_defaultDurationSeconds, 5);
            return Show(NotificationType.Warning, title, message, dur, action, actionLabel);
        }

        public NotificationItem ShowError(
            string title,
            string message,
            int? durationSeconds = null,
            ICommand? action = null,
            string? actionLabel = null)
        {
            // Errors get longer visibility (default 8s) if not explicitly specified
            int dur = durationSeconds ?? Math.Max(_defaultDurationSeconds, 8);
            return Show(NotificationType.Error, title, message, dur, action, actionLabel);
        }

        public NotificationItem Show(
            NotificationType type,
            string title,
            string message,
            int? durationSeconds = null,
            ICommand? action = null,
            string? actionLabel = null)
        {
            int dur = durationSeconds ?? _defaultDurationSeconds;

            var item = new NotificationItem
            {
                Type = type,
                Title = title,
                Message = message,
                DurationSeconds = dur,
                ActionCommand = action,
                ActionLabel = actionLabel,
                IsRead = false
            };

            DispatchToUI(() =>
            {
                // Add to history (newest at index 0)
                _history.Insert(0, item);
                while (_history.Count > MaxHistoryItems)
                {
                    var oldest = _history[_history.Count - 1];
                    _history.RemoveAt(_history.Count - 1);
                    oldest.Dispose();
                }

                // Evict oldest toast if active stack is full
                while (_activeToasts.Count >= MaxActiveToasts)
                {
                    var evicted = _activeToasts[0];
                    _activeToasts.RemoveAt(0);
                    evicted.CancelAutoDismiss();
                }

                // Add to active floating toasts
                _activeToasts.Add(item);
                NotificationAdded?.Invoke(item);
            });

            // Schedule auto-dismiss if duration > 0
            if (dur > 0)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(dur), item.CancellationToken);
                        DismissToast(item.Id);
                    }
                    catch (OperationCanceledException)
                    {
                        // Dismissed manually or cancelled
                    }
                });
            }

            return item;
        }

        public void DismissToast(Guid id)
        {
            DispatchToUI(() =>
            {
                var found = _activeToasts.FirstOrDefault(t => t.Id == id);
                if (found != null)
                {
                    _activeToasts.Remove(found);
                    found.CancelAutoDismiss();
                    ToastDismissed?.Invoke(found);
                }
            });
        }

        public void DismissToast(NotificationItem item)
        {
            if (item == null) return;
            DismissToast(item.Id);
        }

        public void MarkAllAsRead()
        {
            DispatchToUI(() =>
            {
                foreach (var item in _history)
                {
                    item.IsRead = true;
                }
            });
        }

        public void ClearHistory()
        {
            DispatchToUI(() =>
            {
                foreach (var item in _activeToasts)
                {
                    item.CancelAutoDismiss();
                }
                _activeToasts.Clear();

                foreach (var item in _history)
                {
                    item.Dispose();
                }
                _history.Clear();
            });
        }

        private void DispatchToUI(Action action)
        {
            var app = Application.Current;
            if (app != null && app.Dispatcher != null && !app.Dispatcher.HasShutdownStarted)
            {
                if (app.Dispatcher.CheckAccess())
                {
                    lock (_syncLock)
                    {
                        action();
                    }
                }
                else
                {
                    // Non-blocking: a background caller must never wait on the UI thread (shutdown deadlock, 2026-11-20).
                    app.Dispatcher.BeginInvoke(() =>
                    {
                        lock (_syncLock)
                        {
                            action();
                        }
                    }, DispatcherPriority.Normal);
                }
            }
            else
            {
                // Headless xUnit testing environment
                lock (_syncLock)
                {
                    action();
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            ClearHistory();
        }
    }
}
