namespace RevitAgent.Core.Notify;

/// <summary>
/// Gateway notification channel interface.
/// </summary>
public interface INotifier
{
    Task NotifyAsync(NotificationKind kind, string text, IReadOnlyList<string>? imagePaths = null, CancellationToken ct = default);
}
