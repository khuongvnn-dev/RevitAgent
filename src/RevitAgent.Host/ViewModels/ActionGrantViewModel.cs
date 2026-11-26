namespace RevitAgent.Host.ViewModels
{
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.Globalization;
    using System.Linq;
    using CommunityToolkit.Mvvm.ComponentModel;
    using CommunityToolkit.Mvvm.Input;
    using RevitAgent.Core.Policy;
    using RevitAgent.Host.Models;
    using RevitAgent.Host.Services;

    /// <summary>A duration choice of the action-grant picker (null = until turned off).</summary>
    public sealed record GrantDurationOption(string Label, TimeSpan? Duration);

    /// <summary>
    /// One "Allow AI" row: a Revit the gateway did not launch (or the global "all my Revit" row when
    /// <see cref="Pid"/> is null). Reads its state from the <see cref="ActionGrantStore"/> on every tick.
    /// </summary>
    public sealed partial class GrantRow : ObservableObject
    {
        private readonly ActionGrantStore _store;
        private readonly Func<string, string> _l;
        private readonly Func<DateTimeOffset> _now;

        public GrantRow(ActionGrantStore store, int? pid, string label, IReadOnlyList<GrantDurationOption> options, Func<string, string> localize, Func<DateTimeOffset> now)
        {
            _store = store;
            Pid = pid;
            Label = label;
            Options = options;
            _selectedDuration = options[1];
            _l = localize;
            _now = now;
            Refresh();
        }

        public int? Pid { get; }

        public string Label { get; }

        public IReadOnlyList<GrantDurationOption> Options { get; }

        [ObservableProperty]
        private GrantDurationOption _selectedDuration;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsNotGranted))]
        private bool _isGranted;

        [ObservableProperty]
        private string _countdown = string.Empty;

        public bool IsNotGranted => !IsGranted;

        public ActionGrant? Current =>
            Pid is int p
                ? _store.Active().FirstOrDefault(g => g.Scope == GrantScope.Pid && g.Pid == p)
                : _store.Active().FirstOrDefault(g => g.Scope == GrantScope.All);

        public void Refresh()
        {
            var grant = Current;
            IsGranted = grant is not null;
            Countdown = grant is null
                ? string.Empty
                : grant.ExpiresAt is not { } end
                    ? _l("Str_GrantUntilOff")
                    : string.Format(CultureInfo.CurrentCulture, _l("Str_GrantRemainingFmt"), FormatRemaining(end - _now()));
        }

        public static string FormatRemaining(TimeSpan t)
        {
            if (t < TimeSpan.Zero)
            {
                t = TimeSpan.Zero;
            }

            return t.TotalHours >= 1
                ? string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds)
                : string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}", t.Minutes, t.Seconds);
        }

        [RelayCommand]
        private void Grant()
        {
            _store.Grant(Pid is null ? GrantScope.All : GrantScope.Pid, Pid, SelectedDuration.Duration);
            Refresh();
        }

        [RelayCommand]
        private void Revoke()
        {
            _store.Revoke(Pid is null ? GrantScope.All : GrantScope.Pid, Pid);
            Refresh();
        }
    }

    /// <summary>
    /// Dashboard section "Let AI act on my Revit": lets the owner allow agents to ACT (not just read) on Revit
    /// instances the gateway did not launch, for a limited time. Grants live in Host memory only (lost on restart).
    /// </summary>
    public sealed class ActionGrantViewModel : ObservableObject
    {
        private readonly ActionGrantStore _store;
        private readonly ILocalizationService _localization;
        private readonly Func<DateTimeOffset> _now;

        public ActionGrantViewModel(ActionGrantStore store, ILocalizationService localization, Func<DateTimeOffset>? now = null)
        {
            _store = store;
            _localization = localization;
            _now = now ?? (() => DateTimeOffset.UtcNow);
            Rows = new ObservableCollection<GrantRow> { NewRow(null, _localization.GetString("Str_GrantAllRevit")) };
            _store.Changed += () => Tick();
        }

        /// <summary>First row = all of the owner's Revit; then one row per Revit not launched by the gateway.</summary>
        public ObservableCollection<GrantRow> Rows { get; }

        public bool HasInstanceRows => Rows.Count > 1;

        private IReadOnlyList<GrantDurationOption> Options() => new[]
        {
            new GrantDurationOption(_localization.GetString("Str_Grant15m"), TimeSpan.FromMinutes(15)),
            new GrantDurationOption(_localization.GetString("Str_Grant1h"), TimeSpan.FromHours(1)),
            new GrantDurationOption(_localization.GetString("Str_Grant4h"), TimeSpan.FromHours(4)),
            new GrantDurationOption(_localization.GetString("Str_GrantUntilOff"), null),
        };

        private GrantRow NewRow(int? pid, string label) =>
            new(_store, pid, label, Options(), key => _localization.GetString(key), _now);

        /// <summary>Called with every instance snapshot: rebuilds the per-Revit rows and drops grants of Revit processes that are gone.</summary>
        public void Update(IReadOnlyList<RevitInstanceItem> instances)
        {
            var foreign = instances.Where(i => !i.LaunchedByGateway).OrderBy(i => i.Pid).ToList();
            var live = foreign.Select(i => i.Pid).ToHashSet();
            foreach (var grant in _store.Active().Where(g => g.Scope == GrantScope.Pid && g.Pid is int p && !live.Contains(p)).ToList())
            {
                _store.Revoke(GrantScope.Pid, grant.Pid);
            }

            var wanted = foreign.Select(i => (i.Pid, Label: string.Format(CultureInfo.CurrentCulture, "Revit {0} - pid {1} {2}", i.YearText, i.Pid, i.Title).Trim())).ToList();
            var have = Rows.Skip(1).Select(r => (r.Pid, r.Label)).ToList();
            if (wanted.Select(w => (int?)w.Pid).SequenceEqual(have.Select(h => h.Pid)) && wanted.Select(w => w.Label).SequenceEqual(have.Select(h => h.Label)))
            {
                return;
            }

            var keep = Rows[0];
            Rows.Clear();
            Rows.Add(keep);
            foreach (var (pid, label) in wanted)
            {
                Rows.Add(NewRow(pid, label));
            }

            OnPropertyChanged(nameof(HasInstanceRows));
        }

        /// <summary>1 s tick: refreshes countdowns and expiry.</summary>
        public void Tick()
        {
            foreach (var row in Rows)
            {
                row.Refresh();
            }
        }
    }
}
