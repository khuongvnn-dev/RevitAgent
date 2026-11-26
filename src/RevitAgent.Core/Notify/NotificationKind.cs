namespace RevitAgent.Core.Notify;

/// <summary>
/// Kinds of events sent via Telegram notifications.
/// </summary>
public enum NotificationKind
{
    RunResult,
    RevitHang,
    RevitCrash,
    AddinBlocked,
    AgentNeedsHuman,

    /// <summary>The gateway pressed a safe-dismiss button (Close / Cancel / Later) itself to unblock one of its operations (notify only).</summary>
    DialogDismissed
}
