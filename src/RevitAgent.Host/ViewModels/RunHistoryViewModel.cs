using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RevitAgent.Core.RunStore;
using RevitAgent.Host.Models;
using RevitAgent.Host.Services;

namespace RevitAgent.Host.ViewModels;

public partial class RunHistoryViewModel : ObservableObject
{
    private readonly RunStore _runStore;
    private readonly IAuditLogService? _auditLogService;
    private readonly ILocalizationService? _localizationService;

    [ObservableProperty]
    private string _scenarioFilter = string.Empty;

    [ObservableProperty]
    private string _currentVerdictFilter = "ALL";

    [ObservableProperty]
    private int _runCount = 0;

    [ObservableProperty]
    private string _runCountDisplay = "0";

    [ObservableProperty]
    private bool _hasRuns = false;

    public ObservableCollection<RunHistoryItem> Runs { get; } = new();

    public ICollectionView RunsView { get; }

    public RunHistoryViewModel(
        RunStore runStore,
        IAuditLogService? auditLogService = null,
        ILocalizationService? localizationService = null)
    {
        _runStore = runStore ?? throw new ArgumentNullException(nameof(runStore));
        _auditLogService = auditLogService;
        _localizationService = localizationService;

        RunsView = CollectionViewSource.GetDefaultView(Runs);
        RunsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(RunHistoryItem.DateGroup)));
        RunsView.Filter = FilterRunItem;

        Refresh();
    }

    partial void OnScenarioFilterChanged(string value)
    {
        RunsView.Refresh();
        UpdateCountDisplay();
    }

    partial void OnCurrentVerdictFilterChanged(string value)
    {
        RunsView.Refresh();
        UpdateCountDisplay();
    }

    [RelayCommand]
    public void FilterVerdict(string verdict)
    {
        CurrentVerdictFilter = string.IsNullOrWhiteSpace(verdict) ? "ALL" : verdict.Trim().ToUpperInvariant();
    }

    [RelayCommand]
    public void Refresh()
    {
        try
        {
            var summaries = _runStore.ListRuns(new RunFilter { Limit = 500 });
            Runs.Clear();
            foreach (var s in summaries)
            {
                Runs.Add(new RunHistoryItem
                {
                    Id = s.Id,
                    Scenario = s.Scenario,
                    Trigger = s.Trigger,
                    Verdict = s.Verdict,
                    StartedAt = s.StartedAt,
                    EndedAt = s.EndedAt,
                    RunDir = s.RunDir,
                    ReportHtml = s.ReportHtml,
                    ReportMd = s.ReportMd,
                });
            }

            RunsView.Refresh();
            UpdateCountDisplay();
        }
        catch (Exception ex)
        {
            _auditLogService?.AddLog("ERROR", "RunStore Refresh Failed", ex.Message);
        }
    }

    [RelayCommand]
    public void OpenReport(RunHistoryItem? item)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.ReportHtml) || !File.Exists(item.ReportHtml))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = item.ReportHtml,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _auditLogService?.AddLog("ERROR", "Open Report Failed", ex.Message);
        }
    }

    [RelayCommand]
    public void OpenFolder(RunHistoryItem? item)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.RunDir) || !Directory.Exists(item.RunDir))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{item.RunDir}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _auditLogService?.AddLog("ERROR", "Open Run Folder Failed", ex.Message);
        }
    }

    private bool FilterRunItem(object obj)
    {
        if (obj is not RunHistoryItem item) return false;

        if (!string.IsNullOrWhiteSpace(ScenarioFilter) &&
            !item.Scenario.Contains(ScenarioFilter, StringComparison.OrdinalIgnoreCase) &&
            !item.Id.Contains(ScenarioFilter, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.Equals(CurrentVerdictFilter, "ALL", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.Equals(item.Verdict, CurrentVerdictFilter, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private void UpdateCountDisplay()
    {
        var count = 0;
        foreach (var _ in RunsView)
        {
            count++;
        }

        RunCount = count;
        HasRuns = count > 0;
        var unit = _localizationService?.CurrentLanguage == LocalizationService.CultureEnglish ? "runs" : "lần chạy";
        RunCountDisplay = $"{count} {unit}";
    }
}
