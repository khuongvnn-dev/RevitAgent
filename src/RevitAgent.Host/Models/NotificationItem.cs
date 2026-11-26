namespace RevitAgent.Host.Models
{
    using System;
    using System.Threading;
    using System.Windows.Input;
    using CommunityToolkit.Mvvm.ComponentModel;

    /// <summary>
    /// Represents an in-app notification item with support for auto-dismiss timers,
    /// cancellation, and interactive action execution.
    /// </summary>
    public sealed class NotificationItem : ObservableObject, IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private bool _isRead;
        private bool _disposed;

        public Guid Id { get; } = Guid.NewGuid();

        public NotificationType Type { get; init; } = NotificationType.Info;

        public string Title { get; init; } = string.Empty;

        public string Message { get; init; } = string.Empty;

        public DateTime Timestamp { get; } = DateTime.Now;

        public string FormattedTime => Timestamp.ToString("HH:mm:ss");

        public string FormattedFullTime => Timestamp.ToString("HH:mm:ss dd/MM");

        public int DurationSeconds { get; init; } = 3;

        public ICommand? ActionCommand { get; init; }

        public string? ActionLabel { get; init; }

        public bool HasAction => ActionCommand != null && !string.IsNullOrWhiteSpace(ActionLabel);

        public bool IsRead
        {
            get => _isRead;
            set => SetProperty(ref _isRead, value);
        }

        public string IconGlyph => Type switch
        {
            NotificationType.Success => "✓",
            NotificationType.Info => "ℹ",
            NotificationType.Warning => "⚠",
            NotificationType.Error => "⛔",
            _ => "ℹ"
        };

        public string TypeBrushKey => Type switch
        {
            NotificationType.Success => "BrushSuccessDot",
            NotificationType.Info => "BrushAccentPrimary",
            NotificationType.Warning => "BrushWarningDot",
            NotificationType.Error => "BrushDangerDot",
            _ => "BrushAccentPrimary"
        };

        public string TypeBorderKey => Type switch
        {
            NotificationType.Success => "BrushSuccessBorder",
            NotificationType.Info => "BrushAccentBorder",
            NotificationType.Warning => "BrushWarningBorder",
            NotificationType.Error => "BrushDangerBorder",
            _ => "BrushAccentBorder"
        };

        public CancellationToken CancellationToken => _cts.Token;

        public void CancelAutoDismiss()
        {
            try
            {
                if (!_cts.IsCancellationRequested)
                {
                    _cts.Cancel();
                }
            }
            catch (ObjectDisposedException)
            {
                // Ignored
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            CancelAutoDismiss();
            _cts.Dispose();
        }
    }
}
