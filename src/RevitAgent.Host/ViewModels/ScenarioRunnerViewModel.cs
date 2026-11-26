using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RevitAgent.Core.Machine;
using RevitAgent.Core.RunStore;
using RevitAgent.Core.Scenario;
using RevitAgent.Core.Schedule;
using RevitAgent.Host.Models;
using RevitAgent.Host.Services;

namespace RevitAgent.Host.ViewModels;

public partial class ScenarioRunnerViewModel : ObservableObject
{
    private readonly ScenarioLibraryService _libraryService;
    private readonly ScenarioBatchRunner _batchRunner;
    private readonly ScheduleService _scheduleService;
    private readonly ScheduleConfigStore _configStore;
    private readonly MachineReadiness _machineReadiness;
    private readonly IAuditLogService? _auditLogService;
    private readonly IServiceProvider _services;

    public ObservableCollection<ScenarioItemViewModel> Scenarios { get; } = new();

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _currentScenario = string.Empty;

    [ObservableProperty]
    private string _currentStep = string.Empty;

    [ObservableProperty]
    private string _elapsedText = "00:00";

    [ObservableProperty]
    private string _statusMessage = "Ready to run scenarios.";

    [ObservableProperty]
    private double _progressPercent;

    [ObservableProperty]
    private string _progressText = "0/0";

    [ObservableProperty]
    private string? _lastRunDir;

    [ObservableProperty]
    private string? _lastRunId;

    [ObservableProperty]
    private bool _hasLastRun;

    [ObservableProperty]
    private string _libraryDirDisplay = string.Empty;

    // Schedule properties
    [ObservableProperty]
    private bool _scheduleEnabled;

    [ObservableProperty]
    private string _scheduleTime = "22:00";

    [ObservableProperty]
    private bool _dayMon = true;

    [ObservableProperty]
    private bool _dayTue = true;

    [ObservableProperty]
    private bool _dayWed = true;

    [ObservableProperty]
    private bool _dayThu = true;

    [ObservableProperty]
    private bool _dayFri = true;

    [ObservableProperty]
    private bool _daySat;

    [ObservableProperty]
    private bool _daySun;

    [ObservableProperty]
    private int _scheduleRevitYear = 2024;

    [ObservableProperty]
    private string _nextRunTimeText = "--";

    public ScenarioRunnerViewModel(
        ScenarioLibraryService libraryService,
        ScenarioBatchRunner batchRunner,
        ScheduleService scheduleService,
        ScheduleConfigStore configStore,
        MachineReadiness machineReadiness,
        IServiceProvider services,
        IAuditLogService? auditLogService = null)
    {
        _libraryService = libraryService ?? throw new ArgumentNullException(nameof(libraryService));
        _batchRunner = batchRunner ?? throw new ArgumentNullException(nameof(batchRunner));
        _scheduleService = scheduleService ?? throw new ArgumentNullException(nameof(scheduleService));
        _configStore = configStore ?? throw new ArgumentNullException(nameof(configStore));
        _machineReadiness = machineReadiness ?? throw new ArgumentNullException(nameof(machineReadiness));
        _services = services;
        _auditLogService = auditLogService;

        _batchRunner.Progress += OnBatchProgress;

        LoadScheduleConfig();
        RefreshLibrary();
    }

    public void RefreshLibrary()
    {
        LibraryDirDisplay = _libraryService.LibraryDir;
        var selectedDirs = Scenarios.Where(s => s.IsSelected).Select(s => s.Directory).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Scenarios.Clear();
        var items = _libraryService.ListScenarios();
        foreach (var item in items)
        {
            var vm = new ScenarioItemViewModel(item)
            {
                IsSelected = selectedDirs.Contains(item.Directory),
            };
            Scenarios.Add(vm);
        }

        UpdateNextRunTime();
    }

    private void LoadScheduleConfig()
    {
        var cfg = _scheduleService.GetConfig();
        ScheduleEnabled = cfg.Enabled;
        ScheduleTime = string.IsNullOrWhiteSpace(cfg.Time) ? "22:00" : cfg.Time;
        ScheduleRevitYear = cfg.RevitYear > 0 ? cfg.RevitYear : 2024;

        var days = cfg.Days ?? new List<string>();
        DayMon = ScheduleService.IsDayMatching(DayOfWeek.Monday, days);
        DayTue = ScheduleService.IsDayMatching(DayOfWeek.Tuesday, days);
        DayWed = ScheduleService.IsDayMatching(DayOfWeek.Wednesday, days);
        DayThu = ScheduleService.IsDayMatching(DayOfWeek.Thursday, days);
        DayFri = ScheduleService.IsDayMatching(DayOfWeek.Friday, days);
        DaySat = ScheduleService.IsDayMatching(DayOfWeek.Saturday, days);
        DaySun = ScheduleService.IsDayMatching(DayOfWeek.Sunday, days);

        UpdateNextRunTime();
    }

    private void UpdateNextRunTime()
    {
        var next = _scheduleService.CalculateNextRunTime(DateTimeOffset.Now);
        NextRunTimeText = next.HasValue
            ? next.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
            : "--";
    }

    [RelayCommand]
    public void SelectAll()
    {
        foreach (var s in Scenarios)
        {
            s.IsSelected = true;
        }
    }

    [RelayCommand]
    public void SelectNone()
    {
        foreach (var s in Scenarios)
        {
            s.IsSelected = false;
        }
    }

    [RelayCommand]
    public async Task RunBatchAsync()
    {
        var selected = Scenarios.Where(s => s.IsSelected).Select(s => s.Directory).ToList();
        if (selected.Count == 0)
        {
            StatusMessage = "Select at least one scenario to run.";
            return;
        }

        // Check machine readiness first
        StatusMessage = "Checking machine readiness...";
        var report = await _machineReadiness.CheckAsync(new MachineCheckOptions { Year = ScheduleRevitYear });
        var failed = report.Items.Where(i => string.Equals(i.Status, MachineCheckStatus.Fail, StringComparison.OrdinalIgnoreCase)).ToList();

        if (failed.Count > 0)
        {
            var msg = $"The machine is not ready ({failed.Count} failing checks):\n" +
                      string.Join("\n", failed.Select(f => $"- {f.Title}: {f.Detail}")) +
                      "\n\nIgnore this and run the scenarios anyway?";
            var confirm = MessageBox.Show(msg, "Machine readiness warning", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes)
            {
                StatusMessage = "Scenario run cancelled.";
                return;
            }
        }

        IsRunning = true;
        StatusMessage = $"Starting {selected.Count} scenarios...";

        var request = new BatchRequest
        {
            ScenarioDirs = selected,
            Trigger = "manual",
            RevitYear = ScheduleRevitYear,
            IgnoreReadinessErrors = true,
            SessionId = "dashboard",
        };

        try
        {
            var result = await _batchRunner.RunBatchAsync(request);
            if (result.Cancelled)
            {
                StatusMessage = "The run was stopped by the user.";
            }
            else if (!result.Success)
            {
                StatusMessage = $"Scenario run failed: {result.ErrorMessage}";
            }
            else
            {
                StatusMessage = $"Run finished ({result.Runs.Count} scenarios).";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Unexpected error: {ex.Message}";
            _auditLogService?.AddLog("ERROR", "Batch Execution Failed", ex.Message);
        }
        finally
        {
            IsRunning = false;
            RefreshLibrary();
        }
    }

    [RelayCommand]
    public void StopBatch()
    {
        if (IsRunning)
        {
            StatusMessage = "Sending the stop request...";
            _batchRunner.Cancel();
        }
    }

    [RelayCommand]
    public void ViewRunHistory()
    {
        var mainVm = _services.GetService(typeof(MainViewModel)) as MainViewModel;
        if (mainVm != null)
        {
            // Switch to Run History Tab (Index 4)
            mainVm.SelectedTabIndex = 4;
        }
    }

    [RelayCommand]
    public void OpenLastRunFolder()
    {
        if (!string.IsNullOrWhiteSpace(LastRunDir) && Directory.Exists(LastRunDir))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = LastRunDir,
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                _auditLogService?.AddLog("ERROR", "Open Run Folder Failed", ex.Message);
            }
        }
    }

    [RelayCommand]
    public void SaveSchedule()
    {
        var days = new List<string>();
        if (DayMon) days.Add("Mon");
        if (DayTue) days.Add("Tue");
        if (DayWed) days.Add("Wed");
        if (DayThu) days.Add("Thu");
        if (DayFri) days.Add("Fri");
        if (DaySat) days.Add("Sat");
        if (DaySun) days.Add("Sun");

        var selectedScenarios = Scenarios.Where(s => s.IsSelected).Select(s => s.Directory).ToList();
        var existing = _scheduleService.GetConfig();

        var updated = new ScheduleConfig
        {
            Enabled = ScheduleEnabled,
            Time = string.IsNullOrWhiteSpace(ScheduleTime) ? "22:00" : ScheduleTime.Trim(),
            Days = days,
            Scenarios = selectedScenarios.Count > 0 ? selectedScenarios : existing.Scenarios,
            RevitYear = ScheduleRevitYear,
            LastFired = existing.LastFired,
            LastMissedNotified = existing.LastMissedNotified,
        };

        _scheduleService.SaveConfig(updated);
        UpdateNextRunTime();
        StatusMessage = "Overnight schedule settings saved.";
    }

    private void OnBatchProgress(object? sender, BatchProgressEventArgs e)
    {
        Application.Current?.Dispatcher?.Invoke(() =>
        {
            CurrentScenario = e.CurrentScenario ?? string.Empty;
            CurrentStep = e.CurrentStep ?? string.Empty;
            ElapsedText = $"{Math.Floor(e.Elapsed.TotalMinutes):00}:{e.Elapsed.Seconds:00}";
            if (!string.IsNullOrEmpty(e.StatusMessage))
            {
                StatusMessage = e.StatusMessage;
            }

            ProgressText = $"{e.ScenarioIndex}/{e.TotalScenarios}";
            ProgressPercent = e.TotalScenarios > 0 ? (double)e.ScenarioIndex / e.TotalScenarios * 100 : 0;

            if (e.LastRunSummary != null)
            {
                LastRunDir = e.LastRunSummary.RunDir;
                LastRunId = e.LastRunSummary.RunId;
                HasLastRun = true;
            }
        });
    }
}
