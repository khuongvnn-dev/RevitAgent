using System;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using RevitAgent.Core.Scenario;

namespace RevitAgent.Host.Models;

public partial class ScenarioItemViewModel : ObservableObject
{
    public ScenarioLibraryItem Item { get; }

    [ObservableProperty]
    private bool _isSelected;

    public ScenarioItemViewModel(ScenarioLibraryItem item)
    {
        Item = item;
    }

    public string Name => Item.Name;

    public string Suite => Item.Suite;

    public string Directory => Item.Directory;

    public int? RevitVersion => Item.RevitVersion;

    public string? Description => Item.Description;

    public string LastVerdict => Item.LastVerdict ?? "--";

    public string? LastRunId => Item.LastRunId;

    public DateTimeOffset? LastRunTime => Item.LastRunTime;

    public string FormattedLastRun => LastRunTime.HasValue
        ? LastRunTime.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
        : "--";

    public Brush VerdictBrush
    {
        get
        {
            var v = (Item.LastVerdict ?? string.Empty).ToUpperInvariant();
            return v switch
            {
                "PASS" => new SolidColorBrush(Color.FromRgb(16, 185, 129)),       // Emerald Green #10B981
                "FAIL" => new SolidColorBrush(Color.FromRgb(239, 68, 68)),        // Red #EF4444
                "SUSPECT" => new SolidColorBrush(Color.FromRgb(245, 158, 11)),    // Amber #F59E0B
                "ERROR" => new SolidColorBrush(Color.FromRgb(239, 68, 68)),      // Red #EF4444
                "SKIPPED" => new SolidColorBrush(Color.FromRgb(156, 163, 175)),  // Gray #9CA3AF
                _ => new SolidColorBrush(Color.FromRgb(107, 114, 128)),           // Gray #6B7280
            };
        }
    }
}
