namespace RevitAgent.Host.ViewModels
{
    using System;
    using System.Collections.ObjectModel;
    using System.Globalization;
    using System.Linq;
    using System.Windows;
    using CommunityToolkit.Mvvm.ComponentModel;
    using CommunityToolkit.Mvvm.Input;
    using RevitAgent.Core.Policy;
    using RevitAgent.Host.Services;

    /// <summary>One request on the dashboard card: "agent asks to install the Bridge", Approve / Deny.</summary>
    public sealed class ApprovalRow
    {
        public ApprovalRow(PendingApproval item, string text, Action<PendingApproval, bool> resolve)
        {
            Item = item;
            Text = text;
            ApproveCommand = new RelayCommand(() => resolve(item, true));
            DenyCommand = new RelayCommand(() => resolve(item, false));
        }

        public PendingApproval Item { get; }

        public string Text { get; }

        public IRelayCommand ApproveCommand { get; }

        public IRelayCommand DenyCommand { get; }
    }

    /// <summary>
    /// Dashboard card for owner approvals (bridge_install / bridge_uninstall asked by an agent). Non-modal: requests
    /// appear as a card (plus a toast) and auto-deny after 5 minutes. Attaching marks the dashboard channel available.
    /// </summary>
    public sealed partial class OwnerApprovalViewModel : ObservableObject
    {
        private readonly DashboardApprovalChannel _channel;
        private readonly ILocalizationService _localization;
        private readonly INotificationService? _notifications;

        [ObservableProperty]
        private bool _hasPending;

        public OwnerApprovalViewModel(DashboardApprovalChannel channel, ILocalizationService localization, INotificationService? notifications = null)
        {
            _channel = channel;
            _localization = localization;
            _notifications = notifications;
            _channel.Changed += () => OnUi(Refresh);
            _channel.Attach();
        }

        public ObservableCollection<ApprovalRow> Rows { get; } = new();

        private void Refresh()
        {
            var pending = _channel.Pending;
            var added = pending.Where(p => Rows.All(r => r.Item != p)).ToList();
            foreach (var gone in Rows.Where(r => !pending.Contains(r.Item)).ToList())
            {
                Rows.Remove(gone);
            }

            foreach (var item in added)
            {
                var text = Describe(item.Request);
                Rows.Add(new ApprovalRow(item, text, _channel.Resolve));
                _notifications?.ShowWarning(_localization.GetString("Str_CardOwnerApproval"), text);
            }

            HasPending = Rows.Count > 0;
        }

        private string Describe(OwnerApprovalRequest request)
        {
            switch (request.Tool)
            {
                case "bridge_install":
                    return string.Format(CultureInfo.CurrentCulture, _localization.GetString("Str_ApprovalInstallFmt"), request.SessionId, request.YearsText);
                case "bridge_uninstall":
                    return string.Format(CultureInfo.CurrentCulture, _localization.GetString("Str_ApprovalUninstallFmt"), request.SessionId, request.YearsText);
                case "revit_close":
                    return string.Format(CultureInfo.CurrentCulture, _localization.GetString("Str_ApprovalCloseRevitFmt"), request.SessionId, request.Detail);
                default:
                    return request.Describe() + "."; // never shown as another request (an unknown tool must not read as "install")
            }
        }

        private static void OnUi(Action action)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess())
            {
                action();
            }
            else
            {
                dispatcher.BeginInvoke(action);
            }
        }
    }
}
